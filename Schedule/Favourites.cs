using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>Title / keyword matching for favourites. Pure, no Jellyfin calls.</summary>
public static class FavouriteMatcher
{
    private static readonly Regex TrailingYear = new(@"\s*\(\d{4}\)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Lower case, accents, punctuation and a leading "The" removed, words kept apart:
    /// "The Twilight Zone (1959)" → "twilight zone", "Law &amp; Order" → "law and order".
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var s = TrailingYear.Replace(value, string.Empty).Replace("&", " and ", StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormD).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                sb.Append(' ');
            }
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] == "the")
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    /// <summary>Whether a guide airing satisfies the rule's title, type, channel and new-only conditions.</summary>
    public static bool Matches(FavouriteRule rule, string? title, bool isMovie, bool isRepeat, string? channelNumber, string? channelName)
    {
        var wanted = Normalize(rule.Title);
        var name = Normalize(title);
        if (wanted.Length == 0 || name.Length == 0)
        {
            return false;
        }

        var titleOk = rule.Match == FavouriteRule.MatchKeyword
            ? (" " + name + " ").Contains(" " + wanted + " ", StringComparison.Ordinal)
            : name == wanted;
        if (!titleOk)
        {
            return false;
        }

        if ((rule.Type == FavouriteRule.TypeMovies && !isMovie) || (rule.Type == FavouriteRule.TypeSeries && isMovie))
        {
            return false;
        }

        if (rule.NewOnly && isRepeat)
        {
            return false;
        }

        var channels = (rule.Channels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return channels.Length == 0 || channels.Any(c =>
            string.Equals(c, channelNumber, StringComparison.OrdinalIgnoreCase)
            || Normalize(c) == Normalize(channelName));
    }
}

/// <summary>One guide airing a favourite matched, and what happens to it.</summary>
public sealed record FavouriteHit(
    Guid ProgramId,
    string RuleId,
    string RuleTitle,
    string Title,
    string Channel,
    DateTime Start,
    DateTime End,
    string Status)
{
    public const string WillRecord = "will record";
    public const string Scheduled = "scheduled";
    public const string InLibrary = "already in library";
    public const string OtherAiring = "another airing is scheduled";
    public const string Cancelled = "cancelled by hand";
    public const string Decided = "taken off by conflict handling";
}

/// <summary>
/// Turns favourites into recordings: for each enabled rule, every upcoming guide airing that matches
/// gets a one-off timer — one airing per episode, the earliest — unless the episode is already
/// scheduled (by any rule or series recording), already in the library (Episode History's mark),
/// was taken off by conflict handling, or was scheduled by a favourite before and cancelled by hand.
/// Remembers what it scheduled in &lt;data&gt;/jellydvr-schedule/favourites.json.
/// </summary>
public sealed class Favourites
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILibraryManager _libraryManager;
    private readonly GuideMarks _marks;
    private readonly OutcomeStore _outcomes;
    private readonly ILogger<Favourites> _logger;
    private readonly string _file;
    private readonly object _lock = new();
    private List<Created> _created;

    public Favourites(ILibraryManager libraryManager, GuideMarks marks, OutcomeStore outcomes, IApplicationPaths paths, ILogger<Favourites> logger)
    {
        _libraryManager = libraryManager;
        _marks = marks;
        _outcomes = outcomes;
        _logger = logger;
        var dir = Path.Combine(paths.DataPath, "jellydvr-schedule");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "favourites.json");
        _created = Load();
    }

    /// <summary>An airing a favourite scheduled.</summary>
    public sealed class Created
    {
        public Guid ProgramId { get; set; }

        public string RuleId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public DateTime End { get; set; }

        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Rule id per programme a favourite scheduled (for its priority in conflicts).</summary>
    public IReadOnlyDictionary<Guid, string> RuleByProgram()
    {
        lock (_lock)
        {
            return _created.GroupBy(c => c.ProgramId).ToDictionary(g => g.Key, g => g.Last().RuleId);
        }
    }

    public void Remember(FavouriteHit hit)
    {
        lock (_lock)
        {
            _created.RemoveAll(c => c.ProgramId == hit.ProgramId);
            _created.Add(new Created { ProgramId = hit.ProgramId, RuleId = hit.RuleId, Title = hit.Title, End = hit.End, CreatedUtc = DateTime.UtcNow });
            Save();
        }
    }

    /// <summary>
    /// Every upcoming airing the given rules match, with its status. <paramref name="timers"/>: Jellyfin's
    /// current timers.
    /// </summary>
    public List<FavouriteHit> Find(IEnumerable<FavouriteRule> rules, IReadOnlyList<TimerInfoDto> timers)
    {
        var now = DateTime.UtcNow;
        Prune(now);
        var active = rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Title)).ToList();
        if (active.Count == 0)
        {
            return new List<FavouriteHit>();
        }

        var scheduledPrograms = timers
            .Where(t => t.Status is RecordingStatus.New or RecordingStatus.InProgress)
            .Select(t => Guid.TryParse(t.ProgramId, out var id) ? id : Guid.Empty)
            .ToHashSet();
        HashSet<Guid> created;
        lock (_lock)
        {
            created = _created.Select(c => c.ProgramId).ToHashSet();
        }

        var channels = new Dictionary<Guid, LiveTvChannel?>();
        LiveTvChannel? Channel(Guid id) => channels.TryGetValue(id, out var c) ? c : channels[id] = _libraryManager.GetItemById(id) as LiveTvChannel;

        var programs = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.TvProgram },
                MinEndDate = now.AddMinutes(5)
            })
            .OfType<LiveTvProgram>()
            .Where(p => p.EndDate.HasValue)
            .ToList();

        var hits = new List<FavouriteHit>();
        foreach (var rule in active)
        {
            var matches = programs
                .Where(p =>
                {
                    var ch = Channel(p.ChannelId);
                    return FavouriteMatcher.Matches(rule, p.Name, p.IsMovie, p.IsRepeat, ch?.Number, ch?.Name);
                })
                .OrderBy(p => p.StartDate)
                .ToList();

            // One airing per episode: the earliest that can still be scheduled.
            foreach (var episode in matches.GroupBy(p => EpisodeKey(p, _marks)))
            {
                var airings = episode.ToList();
                var anyScheduled = airings.Any(p => scheduledPrograms.Contains(p.Id));
                var inLibrary = rule.SkipInLibrary && airings.Any(p => _marks.IsMarkedInLibrary(p.EpisodeTitle));
                var chosen = false;
                foreach (var p in airings)
                {
                    string status;
                    if (scheduledPrograms.Contains(p.Id))
                    {
                        status = FavouriteHit.Scheduled;
                    }
                    else if (anyScheduled || chosen)
                    {
                        status = FavouriteHit.OtherAiring;
                    }
                    else if (inLibrary)
                    {
                        status = FavouriteHit.InLibrary;
                    }
                    else if (_outcomes.GetOutcome(p.Id) is not null)
                    {
                        status = FavouriteHit.Decided;
                    }
                    else if (created.Contains(p.Id))
                    {
                        status = FavouriteHit.Cancelled; // a favourite scheduled it and the timer is gone
                    }
                    else if (p.StartDate <= now.AddMinutes(1))
                    {
                        continue; // already on: favourites record from the start, not late
                    }
                    else
                    {
                        status = FavouriteHit.WillRecord;
                        chosen = true;
                    }

                    var ch = Channel(p.ChannelId);
                    hits.Add(new FavouriteHit(
                        p.Id,
                        rule.Id,
                        rule.Title,
                        _marks.StripMarks(p.EpisodeTitle) is { Length: > 0 } ep ? p.Name + " - " + ep : p.Name,
                        ch is null ? string.Empty : (ch.Number + " " + ch.Name).Trim(),
                        p.StartDate,
                        p.EndDate!.Value,
                        status));
                }
            }
        }

        // Two rules matching the same airing: keep one.
        return hits.GroupBy(h => h.ProgramId).Select(g => g.First()).OrderBy(h => h.Start).ToList();
    }

    // What makes two airings "the same episode": a real TMS episode/movie id; else series name +
    // season/episode, or name + episode title; a generic airing (news, no episode data) is its own.
    private static string EpisodeKey(LiveTvProgram p, GuideMarks marks)
    {
        if (ConflictPlanner.IsEpisodeId(p.ShowId))
        {
            return "id:" + p.ShowId;
        }

        var name = FavouriteMatcher.Normalize(p.Name);
        if (p.IsMovie)
        {
            return "movie:" + name + "|" + p.ProductionYear?.ToString(CultureInfo.InvariantCulture);
        }

        if (p.ParentIndexNumber is > 0 && p.IndexNumber is > 0)
        {
            return string.Format(CultureInfo.InvariantCulture, "ep:{0}|{1}|{2}", name, p.ParentIndexNumber, p.IndexNumber);
        }

        var title = FavouriteMatcher.Normalize(marks.StripMarks(p.EpisodeTitle));
        return title.Length >= 3 ? "title:" + name + "|" + title : "airing:" + p.Id.ToString("N");
    }

    private void Prune(DateTime now)
    {
        lock (_lock)
        {
            if (_created.RemoveAll(c => c.End < now.AddDays(-1)) > 0)
            {
                Save();
            }
        }
    }

    private List<Created> Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                return JsonSerializer.Deserialize<List<Created>>(File.ReadAllText(_file)) ?? new List<Created>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {File}", _file);
        }

        return new List<Created>();
    }

    private void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_created, JsonOptions));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {File}", _file);
        }
    }
}
