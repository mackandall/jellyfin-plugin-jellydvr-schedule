using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>A recording running right now.</summary>
public sealed record ActiveRecording(string TimerId, Guid ChannelId, string Name, DateTime EndUtc);

/// <summary>
/// The recordings running right now, fast. ILiveTvManager.GetTimers builds a DTO for every timer
/// (5+ s on this server, longer while a channel is starting), too slow for a client waiting on
/// "can this channel start?". Jellyfin's own timer store, <c>&lt;data&gt;/livetv/timers.json</c>, has each
/// timer's status and channel; IRecordingsManager confirms in memory that it's really recording.
/// </summary>
public sealed class ActiveRecordings
{
    private static readonly TimeSpan ChannelCacheAge = TimeSpan.FromMinutes(10);

    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly IRecordingsManager _recordings;
    private readonly ILogger<ActiveRecordings> _logger;
    private readonly object _lock = new();
    private Dictionary<string, Guid> _channels = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _channelsAt = DateTime.MinValue;

    public ActiveRecordings(IApplicationPaths paths, ILibraryManager libraryManager, IRecordingsManager recordings, ILogger<ActiveRecordings> logger)
    {
        _paths = paths;
        _libraryManager = libraryManager;
        _recordings = recordings;
        _logger = logger;
    }

    public IReadOnlyList<ActiveRecording> Current()
    {
        var result = new List<ActiveRecording>();
        try
        {
            var file = Path.Combine(_paths.DataPath, "livetv", "timers.json");
            if (!File.Exists(file))
            {
                return result;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (Str(t, "Status") is not ("InProgress" or "2") || Str(t, "Id") is not { } id
                    || _recordings.GetActiveRecordingPath(id) is null
                    || Str(t, "ChannelId") is not { } externalChannel || ChannelFor(externalChannel) is not { } channelId)
                {
                    continue;
                }

                var end = t.TryGetProperty("EndDate", out var e) && e.TryGetDateTime(out var d) ? d.ToUniversalTime() : DateTime.UtcNow;
                var post = t.TryGetProperty("PostPaddingSeconds", out var p) && p.TryGetInt32(out var s) ? s : 0;
                result.Add(new ActiveRecording(id, channelId, Str(t, "Name") ?? string.Empty, end.AddSeconds(Math.Max(0, post))));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Can't read Jellyfin's timers.json");
        }

        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    // Timer channel ids are the tuner's ("hdhr_259"); map them to library channel ids.
    private Guid? ChannelFor(string externalId)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _channelsAt > ChannelCacheAge || !_channels.ContainsKey(externalId))
            {
                if (DateTime.UtcNow - _channelsAt > TimeSpan.FromSeconds(30))
                {
                    _channels = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.TvChannel } })
                        .OfType<LiveTvChannel>()
                        .Where(c => !string.IsNullOrEmpty(c.ExternalId))
                        .GroupBy(c => c.ExternalId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
                    _channelsAt = DateTime.UtcNow;
                }
            }

            return _channels.TryGetValue(externalId, out var id) ? id : null;
        }
    }
}
