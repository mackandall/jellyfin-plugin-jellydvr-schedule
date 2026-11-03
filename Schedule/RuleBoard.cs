using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// Every recording rule in one list, for the clients: Jellyfin's series recordings and the Recording
/// Favourites, each with its state (recording, paused, off, nothing in the guide), next airings
/// (including ones a pause holds), priority, artwork and what the library has of the show.
/// </summary>
public sealed class RuleBoard
{
    public const string StateRecording = "recording";
    public const string StatePaused = "paused";
    public const string StateOff = "off";
    public const string StateNotInGuide = "not-in-guide";
    public const string StateNothingToRecord = "nothing-to-record";

    private const int AiringsShown = 6;
    // The guide changes with a guide refresh, the library with new recordings: neither needs to be
    // read on every look at the list.
    private static readonly TimeSpan GuideCache = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LibraryCache = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _services;
    private readonly PauseStore _pauses;
    private readonly Favourites _favourites;
    private readonly ILogger<RuleBoard> _logger;
    private readonly object _lock = new();
    // Jellyfin's timer lists take seconds (it builds every timer, cancelled ones too): a snapshot,
    // refreshed in the background when timers change or it's more than a little old. Pauses,
    // favourites and priorities are laid over it fresh on every look.
    private static readonly TimeSpan SnapshotFresh = TimeSpan.FromSeconds(30);
    private Snapshot? _snapshot;
    private Task? _refreshing;
    private volatile bool _dirty;
    private bool _subscribed;
    private (DateTime At, HashSet<string> Names) _guide = (DateTime.MinValue, new HashSet<string>());
    private (DateTime At, Dictionary<string, List<BaseItem>> Shows) _library = (DateTime.MinValue, new Dictionary<string, List<BaseItem>>());
    private readonly Dictionary<string, (DateTime At, int Recorded, int Unwatched, Guid? LibraryId, Image? Image)> _counts = new(StringComparer.Ordinal);

    public RuleBoard(ILibraryManager libraryManager, IServiceProvider services, PauseStore pauses, Favourites favourites, ILogger<RuleBoard> logger)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _services = services;
        _pauses = pauses;
        _favourites = favourites;
    }

    private ILiveTvManager LiveTv => _services.GetRequiredService<ILiveTvManager>();

    public sealed record Airing(DateTime Start, string Channel, string? Episode, bool Held, bool Now);

    public sealed record Image(Guid ItemId, string Type, string? Tag);

    /// <summary>An airing in the next two weeks, for the web's day strip: when, and whether a pause holds it.</summary>
    public sealed record Mark(DateTime Start, bool Held);

    public sealed class Rule
    {
        /// <summary>Series timer id (no dashes) or "fav:" + favourite id: the priority list's key.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>"series" or "favourite".</summary>
        public string Kind { get; set; } = "series";

        public string Name { get; set; } = string.Empty;

        public string State { get; set; } = StateRecording;

        /// <summary>Paused / off until (UTC); null with a paused or off state: until turned back on.</summary>
        public DateTime? Until { get; set; }

        /// <summary>Paused until the guide shows a season premiere.</summary>
        public bool UntilPremiere { get; set; }

        /// <summary>Channel and options, in words.</summary>
        public string Details { get; set; } = string.Empty;

        public int Upcoming { get; set; }

        public int Held { get; set; }

        public List<Airing> Next { get; set; } = new();

        /// <summary>Every airing it records (or holds) in the next 14 days.</summary>
        public List<Mark> Days { get; set; } = new();

        /// <summary>1-based place in the priority order.</summary>
        public int Priority { get; set; }

        public Image? Image { get; set; }

        /// <summary>Episodes (or movies) of the show in the library, and how many are unwatched (for the asking user).</summary>
        public int Recorded { get; set; }

        public int Unwatched { get; set; }

        /// <summary>The library item for the show (its page), if it has one.</summary>
        public Guid? LibraryId { get; set; }
    }

    private sealed record Snapshot(DateTime At, IReadOnlyList<SeriesTimerInfoDto> Series, IReadOnlyList<TimerInfoDto> Timers);

    /// <summary>Timers changed: the next look refreshes the snapshot (in the background).</summary>
    public void Invalidate()
    {
        _dirty = true;
        // Start now, not at the next look: that look would still get the old snapshot.
        if (_snapshot is not null)
        {
            _ = RefreshAsync();
        }
    }

    private async Task<Snapshot> SnapshotAsync(CancellationToken ct)
    {
        Subscribe();
        var snap = _snapshot;
        if (snap is null)
        {
            await RefreshAsync().WaitAsync(ct).ConfigureAwait(false);
            return _snapshot!;
        }

        if (_dirty || DateTime.UtcNow - snap.At > SnapshotFresh)
        {
            _ = RefreshAsync();
        }

        return snap;
    }

    private Task RefreshAsync()
    {
        lock (_lock)
        {
            if (_refreshing is { IsCompleted: false } running)
            {
                return running;
            }

            _dirty = false;
            _refreshing = Task.Run(async () =>
            {
                try
                {
                    var series = (await LiveTv.GetSeriesTimers(new SeriesTimerQuery(), CancellationToken.None).ConfigureAwait(false)).Items;
                    var now = DateTime.UtcNow;
                    var timers = (await LiveTv.GetTimers(new TimerQuery(), CancellationToken.None).ConfigureAwait(false)).Items
                        .Where(t => (t.Status == RecordingStatus.New && t.EndDate > now) || t.Status == RecordingStatus.InProgress)
                        .ToList();
                    _snapshot = new Snapshot(DateTime.UtcNow, series, timers);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read the timers for the recording rules list");
                    _dirty = true;
                }
            });
            // Timers changed while this one was reading: once more when it's done, so the change shows.
            _refreshing.ContinueWith(
                t =>
                {
                    if (_dirty && t.Status == TaskStatus.RanToCompletion && _snapshot is not null)
                    {
                        _ = RefreshAsync();
                    }
                },
                TaskScheduler.Default);
            return _refreshing;
        }
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        var liveTv = LiveTv;
        liveTv.TimerCreated += (_, _) => Invalidate();
        liveTv.TimerCancelled += (_, _) => Invalidate();
        liveTv.SeriesTimerCreated += (_, _) => Invalidate();
        liveTv.SeriesTimerCancelled += (_, _) => Invalidate();
    }

    /// <summary>Starts reading Jellyfin's timers so the first look at the list is quick.</summary>
    public void Warm() => _ = SnapshotAsync(CancellationToken.None);

    public async Task<List<Rule>> BuildAsync(User? user, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var now = DateTime.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var marks = new List<string>();
        void Mark(string what) => marks.Add(what + " " + clock.ElapsedMilliseconds + " ms");
        var snap = await SnapshotAsync(ct).ConfigureAwait(false);
        var seriesTimers = snap.Series;
        var pauses = _pauses.Pauses;
        // Episodes a pause has cancelled since the snapshot are held, not upcoming.
        var heldPrograms = pauses.SelectMany(p => p.Cancelled).Select(h => h.ProgramId.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var timers = snap.Timers
            .Where(t => t.EndDate > now && !(t.Status == RecordingStatus.New && heldPrograms.Contains(PauseStore.Plain(t.ProgramId ?? string.Empty))))
            .ToList();
        var order = ScheduleManager.OrderedEntries(cfg, seriesTimers, pauses)
            .Select((e, i) => (e.Id, i)).ToDictionary(x => PauseStore.Plain(x.Id), x => x.i + 1, StringComparer.OrdinalIgnoreCase);
        Mark("timers");
        var guide = GuideNames(now);
        Mark("guide");
        var library = LibraryShows();
        Mark("library");
        var programs = new Dictionary<Guid, LiveTvProgram?>();
        LiveTvProgram? Program(string? id) => Guid.TryParse(id, out var g)
            ? programs.TryGetValue(g, out var p) ? p : programs[g] = _libraryManager.GetItemById(g) as LiveTvProgram
            : null;
        Airing ToAiring(TimerInfoDto t) => new(t.StartDate, t.ChannelName ?? string.Empty, Program(t.ProgramId)?.EpisodeTitle, false, t.Status == RecordingStatus.InProgress);

        var rules = new List<Rule>();
        foreach (var s in seriesTimers)
        {
            var mine = timers.Where(t => PauseStore.Same(t.SeriesTimerId, s.Id)).ToList();
            var pause = pauses.FirstOrDefault(p => PauseStore.Same(p.SeriesTimerId, s.Id));
            var held = pause?.Cancelled.Where(h => h.Start > now).ToList() ?? new List<PauseStore.Held>();
            var rule = new Rule
            {
                Id = PauseStore.Plain(s.Id),
                Kind = "series",
                Name = s.Name ?? string.Empty,
                Until = pause?.Until,
                UntilPremiere = pause?.UntilPremiere == true,
                Details = string.Join(" · ", new[]
                {
                    s.RecordAnyChannel ? "any channel" : s.ChannelName,
                    s.RecordAnyTime ? null : "this time only",
                    s.RecordNewOnly ? "new episodes only" : "all episodes",
                    s.SkipEpisodesInLibrary ? "skips ones in the library" : null,
                    s.KeepUpTo > 0 ? "keeps the latest " + s.KeepUpTo : null
                }.Where(x => !string.IsNullOrWhiteSpace(x))),
                Upcoming = mine.Count,
                Held = held.Count,
                Next = mine.Select(ToAiring)
                    .Concat(held.Select(h => new Airing(h.Start, ChannelOf(h.ProgramId), Program(h.ProgramId.ToString("N"))?.EpisodeTitle, true, false)))
                    .OrderBy(a => a.Start).Take(AiringsShown).ToList(),
                Days = mine.Select(t => new Mark(t.StartDate, false)).Concat(held.Select(h => new Mark(h.Start, true)))
                    .Where(m => m.Start < now.AddDays(14)).OrderBy(m => m.Start).ToList(),
                Priority = order.TryGetValue(PauseStore.Plain(s.Id), out var rank) ? rank : 0,
                Image = SeriesImage(s)
            };
            rule.State = pause is not null ? StatePaused
                : mine.Count > 0 ? StateRecording
                : guide.Contains(FavouriteMatcher.Normalize(s.Name)) ? StateNothingToRecord
                : StateNotInGuide;
            AddLibrary(rule, library, user, movies: false);
            rules.Add(rule);
        }

        var byProgram = _favourites.RuleByProgram();
        foreach (var f in (cfg.Favourites ?? Array.Empty<FavouriteRule>()).Where(f => !string.IsNullOrWhiteSpace(f.Title)))
        {
            var mine = timers.Where(t => string.IsNullOrEmpty(t.SeriesTimerId) && Guid.TryParse(t.ProgramId, out var p)
                && byProgram.TryGetValue(p, out var r) && r == f.Id).ToList();
            var key = ScheduleManager.FavouriteKey(f.Id);
            var rule = new Rule
            {
                Id = key,
                Kind = "favourite",
                Name = f.Title,
                Until = f.Enabled ? null : f.OffUntil,
                Details = string.Join(" · ", new[]
                {
                    f.Match == FavouriteRule.MatchKeyword ? "keyword" : "exact title",
                    f.Type == FavouriteRule.TypeMovies ? "movies" : f.Type == FavouriteRule.TypeSeries ? "shows" : "shows and movies",
                    string.IsNullOrWhiteSpace(f.Channels) ? "any channel" : "channels " + f.Channels,
                    f.NewOnly ? "new episodes only" : null,
                    f.SkipInLibrary ? "skips ones in the library" : null
                }.Where(x => x is not null)),
                Upcoming = mine.Count,
                Next = mine.Select(ToAiring).OrderBy(a => a.Start).Take(AiringsShown).ToList(),
                Days = mine.Select(t => new Mark(t.StartDate, false)).Where(m => m.Start < now.AddDays(14)).OrderBy(m => m.Start).ToList(),
                Priority = order.TryGetValue(key, out var rank) ? rank : 0
            };
            var wanted = FavouriteMatcher.Normalize(f.Title);
            var inGuide = f.Match == FavouriteRule.MatchKeyword
                ? guide.Any(n => FavouriteMatcher.TitleMatches(wanted, n, keyword: true))
                : guide.Contains(wanted);
            rule.State = !f.Enabled ? StateOff
                : mine.Count > 0 ? StateRecording
                : inGuide ? StateNothingToRecord
                : StateNotInGuide;
            AddLibrary(rule, library, user, movies: f.Type != FavouriteRule.TypeSeries);
            rule.Image ??= mine.Select(t => Program(t.ProgramId)).OfType<LiveTvProgram>().Where(p => p.HasImage(ImageType.Primary))
                .Select(p => new Image(p.Id, "Primary", null)).FirstOrDefault();
            rules.Add(rule);
        }

        Mark("rules");
        if (clock.ElapsedMilliseconds > 2000)
        {
            _logger.LogInformation("Recording rules list was slow: {Marks}", string.Join(", ", marks));
        }

        return rules.OrderBy(r => r.Priority == 0 ? int.MaxValue : r.Priority).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string ChannelOf(Guid programId) =>
        _libraryManager.GetItemById(programId) is LiveTvProgram p ? _libraryManager.GetItemById(p.ChannelId)?.Name ?? string.Empty : string.Empty;

    private static Image? SeriesImage(SeriesTimerInfoDto s)
    {
        if (Guid.TryParse(s.ParentThumbItemId, out var thumb))
        {
            return new Image(thumb, "Thumb", s.ParentThumbImageTag);
        }

        if (Guid.TryParse(s.ParentBackdropItemId, out var backdrop) && s.ParentBackdropImageTags is { Length: > 0 } tags)
        {
            return new Image(backdrop, "Backdrop", tags[0]);
        }

        if (s.ParentPrimaryImageItemId is { } primary && primary != Guid.Empty)
        {
            return new Image(primary, "Primary", s.ParentPrimaryImageTag);
        }

        return s.ImageTags is { } own && own.TryGetValue(ImageType.Primary, out var tag) && Guid.TryParse(s.Id, out var id)
            ? new Image(id, "Primary", tag)
            : null;
    }

    // The library's shows and movies by normalized name (a show can be in several libraries).
    private Dictionary<string, List<BaseItem>> LibraryShows()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _library.At < LibraryCache)
            {
                return _library.Shows;
            }
        }

        var shows = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie }, Recursive = true })
            .GroupBy(i => FavouriteMatcher.Normalize(i.Name))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        lock (_lock)
        {
            _library = (DateTime.UtcNow, shows);
        }

        return shows;
    }

    private void AddLibrary(Rule rule, Dictionary<string, List<BaseItem>> library, User? user, bool movies)
    {
        var key = rule.Id + "|" + user?.Id.ToString("N");
        lock (_lock)
        {
            if (_counts.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < LibraryCache)
            {
                rule.Recorded = c.Recorded;
                rule.Unwatched = c.Unwatched;
                rule.LibraryId = c.LibraryId;
                rule.Image ??= c.Image;
                return;
            }
        }

        CountLibrary(rule, library, user, movies);
        lock (_lock)
        {
            _counts[key] = (DateTime.UtcNow, rule.Recorded, rule.Unwatched, rule.LibraryId, rule.Image);
        }
    }

    private void CountLibrary(Rule rule, Dictionary<string, List<BaseItem>> library, User? user, bool movies)
    {
        if (!library.TryGetValue(FavouriteMatcher.Normalize(rule.Name), out var items))
        {
            return;
        }

        foreach (var item in items)
        {
            if (item is Series series)
            {
                var query = new InternalItemsQuery(user) { IncludeItemTypes = new[] { BaseItemKind.Episode }, AncestorIds = new[] { series.Id }, Recursive = true, IsVirtualItem = false };
                var count = _libraryManager.GetCount(query);
                if (user is not null)
                {
                    rule.Unwatched += _libraryManager.GetCount(new InternalItemsQuery(user) { IncludeItemTypes = new[] { BaseItemKind.Episode }, AncestorIds = new[] { series.Id }, Recursive = true, IsVirtualItem = false, IsPlayed = false });
                }

                if (count > rule.Recorded || rule.LibraryId is null)
                {
                    rule.LibraryId = series.Id;
                }

                rule.Recorded += count;
            }
            else if (movies)
            {
                rule.Recorded++;
                rule.LibraryId ??= item.Id;
                if (user is not null && !item.IsPlayed(user, null))
                {
                    rule.Unwatched++;
                }
            }

            if (rule.Image is null)
            {
                foreach (var type in new[] { ImageType.Thumb, ImageType.Backdrop, ImageType.Primary })
                {
                    if (item.HasImage(type))
                    {
                        rule.Image = new Image(item.Id, type.ToString(), null);
                        break;
                    }
                }
            }
        }
    }

    // Normalized titles of everything in the guide from now on (cached for a couple of minutes).
    private HashSet<string> GuideNames(DateTime now)
    {
        lock (_lock)
        {
            if (now - _guide.At < GuideCache)
            {
                return _guide.Names;
            }
        }

        var names = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.TvProgram }, MinEndDate = now })
            .Select(p => FavouriteMatcher.Normalize(p.Name))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            _guide = (now, names);
        }

        return names;
    }

}
