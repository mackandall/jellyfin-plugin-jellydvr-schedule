using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DvrSchedule.Schedule;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSchedule.Web;

/// <summary>
/// The upcoming recordings as a compact list (the web client's Schedule "List" view), read from
/// Jellyfin's own timer store, &lt;data&gt;/livetv/timers.json, so it answers in milliseconds instead of
/// the seconds ILiveTvManager.GetTimers takes here.
/// </summary>
[ApiController]
[Route("JellyDvrSchedule")]
public sealed class UpcomingController : ControllerBase
{
    private static readonly object CacheLock = new();
    private static Dictionary<string, Guid> _programs = new(StringComparer.Ordinal);
    private static Dictionary<string, LiveTvChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _cacheAt = DateTime.MinValue;

    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly IRecordingsManager _recordings;
    private readonly Favourites _favourites;

    public UpcomingController(IApplicationPaths paths, ILibraryManager libraryManager, IRecordingsManager recordings, Favourites favourites)
    {
        _paths = paths;
        _libraryManager = libraryManager;
        _recordings = recordings;
        _favourites = favourites;
    }

    [HttpGet("Upcoming")]
    [Authorize(Policy = Policies.LiveTvAccess)]
    public IActionResult Upcoming()
    {
        var file = Path.Combine(_paths.DataPath, "livetv", "timers.json");
        if (!System.IO.File.Exists(file))
        {
            return Ok(Array.Empty<object>());
        }

        RefreshMaps();
        var favourites = _favourites.RuleByProgram();
        var ruleTitles = (Plugin.Instance?.Configuration.Favourites ?? Array.Empty<Configuration.FavouriteRule>()).ToDictionary(r => r.Id, r => r.Title, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var list = new List<(DateTime Start, object Row)>();
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var status = Str(t, "Status");
            if (status is not ("New" or "InProgress"))
            {
                continue;
            }

            var end = Date(t, "EndDate");
            if (end < now)
            {
                continue;
            }

            var id = Str(t, "Id") ?? string.Empty;
            var recordingNow = status == "InProgress" && _recordings.GetActiveRecordingPath(id) is not null;
            Guid? programId = null;
            lock (CacheLock)
            {
                if (Str(t, "ProgramId") is { } ext && _programs.TryGetValue(ext, out var pid))
                {
                    programId = pid;
                }
            }

            LiveTvChannel? channel = null;
            lock (CacheLock)
            {
                if (Str(t, "ChannelId") is { } ch)
                {
                    _channels.TryGetValue(ch, out channel);
                }
            }

            string kind = !string.IsNullOrEmpty(Str(t, "SeriesTimerId")) ? "series" : "one-off";
            string? favourite = null;
            if (programId is { } p && favourites.TryGetValue(p, out var ruleId))
            {
                kind = "favourite";
                ruleTitles.TryGetValue(ruleId, out favourite);
            }

            var season = Int(t, "SeasonNumber");
            var episode = Int(t, "EpisodeNumber");
            var start = Date(t, "StartDate");
            list.Add((start, new
            {
                TimerId = id,
                ProgramId = programId,
                Name = Str(t, "Name") ?? string.Empty,
                EpisodeTitle = Str(t, "EpisodeTitle"),
                SeasonEpisode = season > 0 && episode > 0 ? $"S{season}:E{episode}" : null,
                IsMovie = t.TryGetProperty("IsMovie", out var m) && m.ValueKind == JsonValueKind.True,
                IsRepeat = t.TryGetProperty("IsRepeat", out var r) && r.ValueKind == JsonValueKind.True,
                Start = start,
                End = end,
                ChannelId = channel?.Id,
                ChannelNumber = channel?.Number,
                ChannelName = channel?.Name,
                Kind = kind,
                Favourite = favourite,
                RecordingNow = recordingNow
            }));
        }

        return Ok(list.OrderBy(x => x.Start).Select(x => x.Row));
    }

    // Timer ids are the tuner's (programme "229_2026-…_hdhr_229", channel "hdhr_229"): map them to
    // library items. Rebuilt at most every 2 minutes.
    private void RefreshMaps()
    {
        lock (CacheLock)
        {
            if (DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(2))
            {
                return;
            }

            _programs = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.TvProgram },
                    MinEndDate = DateTime.UtcNow
                })
                .Where(p => !string.IsNullOrEmpty(p.ExternalId))
                .GroupBy(p => p.ExternalId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
            _channels = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.TvChannel } })
                .OfType<LiveTvChannel>()
                .Where(c => !string.IsNullOrEmpty(c.ExternalId))
                .GroupBy(c => c.ExternalId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _cacheAt = DateTime.UtcNow;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static DateTime Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetDateTime(out var d) ? d.ToUniversalTime() : DateTime.MinValue;
}
