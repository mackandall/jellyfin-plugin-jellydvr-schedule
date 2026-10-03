using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// Looks ahead over the scheduled recordings for times when more recordings would run than
/// there are tuner inputs, and resolves each conflict by priority: the lower-priority recording
/// moves to another airing of the same episode when one fits, or is skipped. In preview mode it
/// only reports what it would do.
/// </summary>
public sealed class ScheduleManager : BackgroundService
{
    private static readonly TimeSpan EventDelay = TimeSpan.FromSeconds(30);

    /// <summary>How far ahead live viewers count as holding their inputs ("never interrupt viewers").</summary>
    public static readonly TimeSpan ViewerHold = TimeSpan.FromMinutes(5);

    /// <summary>Rank of an airing chosen with "Record this instead": above every other recording.</summary>
    public const int PinnedRank = 1000000;

    private readonly ILogger<ScheduleManager> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IConfigurationManager _config;
    private readonly IApplicationPaths _paths;
    private readonly IServiceProvider _serviceProvider;
    private readonly OutcomeStore _store;
    private readonly LiveViewers _viewers;
    private readonly GuideMarks _marks;
    private readonly Favourites _favourites;
    private readonly ITaskManager _taskManager;
    private Timer? _afterGuide;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ConcurrentQueue<ActionRecord> _recent = new();
    private Timer? _soon;
    private CancellationToken _stopping;

    public ScheduleManager(
        ILogger<ScheduleManager> logger,
        ILibraryManager libraryManager,
        IConfigurationManager config,
        IApplicationPaths paths,
        IServiceProvider serviceProvider,
        OutcomeStore store,
        LiveViewers viewers,
        GuideMarks marks,
        Favourites favourites,
        ITaskManager taskManager)
    {
        _marks = marks;
        _favourites = favourites;
        _taskManager = taskManager;
        _store = store;
        _viewers = viewers;
        _logger = logger;
        _libraryManager = libraryManager;
        _config = config;
        _paths = paths;
        _serviceProvider = serviceProvider;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private ILiveTvManager LiveTv => _serviceProvider.GetRequiredService<ILiveTvManager>();

    public SchedulePlan? LastPlan { get; private set; }

    public IReadOnlyList<ActionRecord> RecentActions => _recent.Reverse().ToList();

    /// <summary>What the favourites matched on the last run.</summary>
    public IReadOnlyList<FavouriteHit> LastFavourites { get; private set; } = Array.Empty<FavouriteHit>();

    // ---- scheduling ------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _soon = new Timer(_ => _ = RunSafeAsync("recordings changed"), null, Timeout.Infinite, Timeout.Infinite);
        // After "Refresh Guide": new airings for the favourites. Waits a few minutes so Episode
        // History has re-marked the guide (its in-library marks decide what favourites skip).
        _afterGuide = new Timer(_ => _ = RunSafeAsync("guide refreshed"), null, Timeout.Infinite, Timeout.Infinite);
        _taskManager.TaskCompleted += OnTaskCompleted;
        LiveTv.TimerCreated += OnScheduleChanged;
        LiveTv.SeriesTimerCreated += OnScheduleChanged;
        LiveTv.SeriesTimerCreated += OnSeriesTimerCreated;
        LiveTv.TimerCancelled += OnScheduleChanged;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSafeAsync("periodic").ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(Config.CheckIntervalMinutes, 5, 1440)), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            LiveTv.TimerCreated -= OnScheduleChanged;
            LiveTv.SeriesTimerCreated -= OnScheduleChanged;
            LiveTv.SeriesTimerCreated -= OnSeriesTimerCreated;
            LiveTv.TimerCancelled -= OnScheduleChanged;
            _soon.Dispose();
            _taskManager.TaskCompleted -= OnTaskCompleted;
            _afterGuide.Dispose();
        }
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (e.Task.ScheduledTask.Key == "RefreshGuide")
        {
            _afterGuide?.Change(TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
        }
    }

    // New or cancelled recordings: plan again shortly (debounced; our own changes fire this too).
    private void OnScheduleChanged(object? sender, EventArgs e) => _soon?.Change(EventDelay, Timeout.InfiniteTimeSpan);

    private async Task RunSafeAsync(string reason)
    {
        try
        {
            await RunAsync(reason, _stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schedule check failed");
        }
    }

    public async Task<SchedulePlan> RunAsync(string reason, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cfg = Config;
            await RunFavouritesAsync(cfg, ct).ConfigureAwait(false);
            var plan = await BuildPlanAsync(cfg, ct).ConfigureAwait(false);
            plan.Reason = reason;
            LastPlan = plan;
            var toDo = plan.Decisions.Count(d => d.Kind != DecisionKind.Unresolvable);
            var restores = plan.Decisions.Count(d => d.Kind == DecisionKind.Restore);
            toDo -= restores;
            if (restores > 0)
            {
                _logger.LogInformation("{Count} skipped recording(s) fit again ({Reason})", restores, reason);
            }

            if (toDo > 0)
            {
                _logger.LogInformation(
                    "Recording conflicts ({Reason}): {Count} recording(s) to move or skip{Mode}",
                    reason, toDo, cfg.Mode == PluginConfiguration.ModeActive ? string.Empty : " (preview: nothing done)");
            }

            if (cfg.Mode == PluginConfiguration.ModeActive && toDo + restores > 0)
            {
                await ExecuteAsync(plan, ct).ConfigureAwait(false);
            }

            return plan;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// "Record this instead" (the pin is already saved): schedules the airing if it isn't, then plans
    /// at once so whatever it now outranks gives way.
    /// </summary>
    public async Task<SchedulePlan> RecordInsteadAsync(Guid programId, OutcomeStore.Outcome? outcome, CancellationToken ct)
    {
        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;
        var scheduled = timers.Any(t => Guid.TryParse(t.ProgramId, out var p) && p == programId
            && (t.Status == RecordingStatus.New || t.Status == RecordingStatus.InProgress));
        if (!scheduled)
        {
            await CreateTimerAsync(programId, outcome?.PrePaddingSeconds, outcome?.PostPaddingSeconds, ct).ConfigureAwait(false);
        }

        _store.RemoveOutcomes(new[] { programId });
        var title = outcome?.Title ?? _libraryManager.GetItemById(programId)?.Name ?? string.Empty;
        _logger.LogInformation("Record this instead: {Title}{Note}", title, scheduled ? " (already scheduled)" : string.Empty);
        Remember(new ActionRecord
        {
            Time = DateTime.UtcNow,
            Title = title,
            Start = (_libraryManager.GetItemById(programId) as LiveTvProgram)?.StartDate ?? DateTime.UtcNow,
            Channel = outcome?.ChannelName ?? string.Empty,
            Action = "record this instead",
            ProgramId = programId,
            Ok = true
        });
        return await RunAsync("record this instead", ct).ConfigureAwait(false);
    }

    // Schedules what the favourites match (one-off timers), before conflicts are planned.
    private async Task RunFavouritesAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var rules = cfg.Favourites ?? Array.Empty<FavouriteRule>();

        // A favourite deleted any way (guide button, Live TV list, settings page) takes its
        // not-yet-started recordings with it, like cancelling a series recording. (Switched off is
        // different: it stops scheduling more but keeps what's scheduled.)
        foreach (var orphan in _favourites.OrphanRuleIds(rules))
        {
            await CancelFavouriteRecordingsAsync(orphan, ct).ConfigureAwait(false);
        }

        if (!rules.Any(r => r.Enabled))
        {
            LastFavourites = Array.Empty<FavouriteHit>();
            return;
        }

        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;
        var hits = _favourites.Find(rules, timers);
        foreach (var hit in hits.Where(h => h.Status == FavouriteHit.WillRecord))
        {
            ct.ThrowIfCancellationRequested();
            var record = new ActionRecord
            {
                Time = DateTime.UtcNow,
                Title = hit.Title,
                Start = hit.Start,
                Channel = hit.Channel,
                Action = "favourite: scheduled (" + hit.RuleTitle + ")",
                ProgramId = hit.ProgramId
            };
            try
            {
                _favourites.Remember(hit); // before the timer: the "recordings changed" re-plan must see it
                await CreateTimerAsync(hit.ProgramId, null, null, ct).ConfigureAwait(false);
                record.Ok = true;
                _logger.LogInformation("Favourite {Rule}: scheduled {Title} at {Start:u} on {Channel}", hit.RuleTitle, hit.Title, hit.Start, hit.Channel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                record.Error = ex.Message;
                _logger.LogWarning(ex, "Favourite {Rule}: could not schedule {Title}", hit.RuleTitle, hit.Title);
            }

            Remember(record);
        }

        LastFavourites = hits;
    }

    /// <summary>Cancels the not-yet-started recordings a (removed) favourite scheduled; returns how many.</summary>
    public async Task<int> CancelFavouriteRecordingsAsync(string ruleId, CancellationToken ct)
    {
        var programs = _favourites.ProgramsOf(ruleId).ToHashSet();
        var cancelled = 0;
        if (programs.Count > 0)
        {
            var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;
            foreach (var t in timers.Where(t => t.Status == RecordingStatus.New && string.IsNullOrEmpty(t.SeriesTimerId)
                && Guid.TryParse(t.ProgramId, out var p) && programs.Contains(p)))
            {
                try
                {
                    await LiveTv.CancelTimer(t.Id).ConfigureAwait(false);
                    cancelled++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not cancel {Name}", t.Name);
                }
            }
        }

        _favourites.ForgetRule(ruleId);
        _logger.LogInformation("Favourite {Rule} removed; {Count} upcoming recording(s) cancelled", ruleId, cancelled);
        return cancelled;
    }

    /// <summary>What the favourites (or one rule being edited) would match now, without scheduling anything.</summary>
    public async Task<List<FavouriteHit>> PreviewFavouritesAsync(IEnumerable<FavouriteRule> rules, CancellationToken ct)
    {
        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;
        return _favourites.Find(rules, timers);
    }

    // ---- planning --------------------------------------------------------------------------

    private async Task<SchedulePlan> BuildPlanAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var horizon = now.AddDays(Math.Clamp(cfg.LookAheadDays, 1, 14));
        _store.Prune(now);
        var pinned = _store.Pins.Select(p => p.ProgramId).ToHashSet();
        var capacity = cfg.TunerInputs > 0 ? cfg.TunerInputs : JellyfinTunerCount();
        var plan = new SchedulePlan { CreatedUtc = now, Mode = cfg.Mode, Capacity = capacity };

        var seriesTimers = (await LiveTv.GetSeriesTimers(new SeriesTimerQuery(), ct).ConfigureAwait(false)).Items;
        plan.Series = OrderedEntries(cfg, seriesTimers);
        if (capacity <= 0)
        {
            plan.Warning = "The tuner count is unlimited in Jellyfin; set \"Tuner inputs\" so conflicts can be found.";
        }

        var buffers = LiveBufferTimerIds();
        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items
            .Where(t => (t.Status == RecordingStatus.New && t.StartDate < horizon && t.EndDate > now) || t.Status == RecordingStatus.InProgress)
            .Where(t => !buffers.Contains(t.Id)) // JellyDVR Live buffers give way on their own
            .ToList();

        var seriesRank = plan.Series.Select((s, i) => (s.Id, Rank: 10000 - i)).ToDictionary(x => x.Id, x => x.Rank, StringComparer.Ordinal);
        var recordings = timers.Select(t => ToPlanned(t, cfg, seriesRank, pinned)).ToList();
        plan.Scheduled = recordings.Count;
        var programIds = recordings.Select(r => r.ProgramId).ToList();

        // "Never interrupt viewers": live TV holds its inputs for the next few minutes like a
        // recording already in progress, so a recording about to start gives way instead.
        if (cfg.LiveViewerPolicy == PluginConfiguration.ViewersFirst)
        {
            recordings.AddRange(_viewers.Current().GroupBy(v => v.ChannelId).Select(g => new PlannedRecording(
                "viewer:" + g.Key.ToString("N"),
                "Live TV: " + string.Join(", ", g.Select(v => v.Describe())),
                g.Key,
                g.First().ChannelName,
                now,
                now.Add(ViewerHold),
                int.MaxValue,
                true,
                null,
                Guid.Empty,
                null,
                null,
                TimeSpan.Zero,
                TimeSpan.Zero)));
        }

        // An airing that is on the schedule again (recorded by hand, or restored) is no longer
        // skipped / moved.
        _store.RemoveOutcomes(programIds);
        var restorable = cfg.RestoreSkipped && cfg.Mode == PluginConfiguration.ModeActive
            ? _store.Outcomes
                .Where(o => o.Kind == OutcomeStore.KindSkipped && o.Start < horizon
                    && (o.Start > now.AddMinutes(2) || (cfg.StartLateWhenFree && o.End > now.AddMinutes(5))))
                .Select(o => FromOutcome(o, cfg, seriesRank, pinned))
                .OfType<PlannedRecording>()
                .ToList()
            : new List<PlannedRecording>();

        Dictionary<string, List<Airing>>? byShow = null;
        IEnumerable<Airing> Alternatives(PlannedRecording r)
        {
            if (string.IsNullOrWhiteSpace(r.ShowId))
            {
                return Enumerable.Empty<Airing>();
            }

            byShow ??= AiringsByShow(now.AddMinutes(5), horizon);
            return byShow.TryGetValue(r.ShowId, out var list) ? list.Where(a => a.ProgramId != r.ProgramId) : Enumerable.Empty<Airing>();
        }

        plan.Decisions = ConflictPlanner.Plan(
            recordings, capacity, cfg.MoveToOtherAiring, Alternatives,
            cfg.RecordEachEpisodeOnce, cfg.KeepCopy == PluginConfiguration.KeepFirstRun, restorable);
        return plan;
    }

    private static int RankFor(string? seriesTimerId, bool isPinned, PluginConfiguration cfg, Dictionary<string, int> seriesRank, string? favouriteRuleId = null) =>
        isPinned ? PinnedRank
        : favouriteRuleId is not null ? (seriesRank.TryGetValue(FavouriteKey(favouriteRuleId), out var fr) ? fr : 1)
        : string.IsNullOrEmpty(seriesTimerId) ? (cfg.OneOffAboveSeries ? 100000 : 5000)
        : seriesRank.TryGetValue(seriesTimerId!, out var r) ? r : 1;

    // A skipped airing as a candidate to put back on the schedule.
    private PlannedRecording? FromOutcome(OutcomeStore.Outcome o, PluginConfiguration cfg, Dictionary<string, int> seriesRank, HashSet<Guid> pinned)
    {
        if (_libraryManager.GetItemById(o.ProgramId) is not LiveTvProgram program || !program.EndDate.HasValue)
        {
            return null; // gone from the guide
        }

        var before = TimeSpan.FromSeconds(Math.Max(0, o.PrePaddingSeconds));
        var after = TimeSpan.FromSeconds(Math.Max(0, o.PostPaddingSeconds));
        return new PlannedRecording(
            "restore:" + o.ProgramId.ToString("N"),
            o.Title,
            program.ChannelId,
            o.ChannelName,
            program.StartDate - before,
            program.EndDate.Value + after,
            RankFor(o.SeriesTimerId, pinned.Contains(o.ProgramId), cfg, seriesRank, _favourites.RuleByProgram().TryGetValue(o.ProgramId, out var fav) ? fav : null),
            false,
            program.ShowId,
            o.ProgramId,
            null,
            o.SeriesTimerId,
            before,
            after,
            program.IsRepeat,
            string.IsNullOrEmpty(o.SeriesTimerId),
            pinned.Contains(o.ProgramId));
    }

    private PlannedRecording ToPlanned(TimerInfoDto t, PluginConfiguration cfg, Dictionary<string, int> seriesRank, HashSet<Guid> pinned)
    {
        var program = Guid.TryParse(t.ProgramId, out var pid) ? _libraryManager.GetItemById(pid) as LiveTvProgram : null;
        var favourite = string.IsNullOrEmpty(t.SeriesTimerId) && _favourites.RuleByProgram().TryGetValue(pid, out var fav) ? fav : null;
        var rank = RankFor(t.SeriesTimerId, pinned.Contains(pid), cfg, seriesRank, favourite);
        var title = string.IsNullOrWhiteSpace(program?.EpisodeTitle) ? t.Name : t.Name + " - " + program!.EpisodeTitle;
        var before = TimeSpan.FromSeconds(Math.Max(0, t.PrePaddingSeconds));
        var after = TimeSpan.FromSeconds(Math.Max(0, t.PostPaddingSeconds));
        return new PlannedRecording(
            t.Id,
            title,
            t.ChannelId,
            t.ChannelName ?? string.Empty,
            t.StartDate - before,
            t.EndDate + after,
            rank,
            t.Status == RecordingStatus.InProgress,
            program?.ShowId,
            pid,
            t.Id,
            t.SeriesTimerId,
            before,
            after,
            program?.IsRepeat ?? false,
            string.IsNullOrEmpty(t.SeriesTimerId) && favourite is null,
            pinned.Contains(pid));
    }

    // Jellyfin starts recording a new series recording's current episode at once, part-way through.
    // A few seconds after it's created (its episodes are scheduled by then), move that episode to a
    // later airing when there is one.
    private void OnSeriesTimerCreated(object? sender, Jellyfin.Data.Events.GenericEventArgs<TimerEventInfo> e)
    {
        var seriesTimerId = e.Argument?.Id;
        var createdUtc = DateTime.UtcNow;
        if (string.IsNullOrEmpty(seriesTimerId) || !Config.MoveRunningEpisode)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _stopping).ConfigureAwait(false);
                await _runLock.WaitAsync(_stopping).ConfigureAwait(false);
                try
                {
                    await MoveRunningEpisodesAsync(seriesTimerId!, createdUtc, _stopping).ConfigureAwait(false);
                }
                finally
                {
                    _runLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Checking the new series recording's running episode failed");
            }
        });
    }

    private async Task MoveRunningEpisodesAsync(string seriesTimerId, DateTime createdUtc, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;
        var running = timers.Where(t => string.Equals(t.SeriesTimerId, seriesTimerId, StringComparison.OrdinalIgnoreCase)
                && t.Status is RecordingStatus.InProgress or RecordingStatus.New
                && t.StartDate < createdUtc.AddMinutes(-2) && t.EndDate > now.AddMinutes(2))
            .ToList();
        if (running.Count == 0)
        {
            return;
        }

        Dictionary<string, List<Airing>>? byShow = null;
        foreach (var t in running)
        {
            if (!Guid.TryParse(t.ProgramId, out var programId) || _libraryManager.GetItemById(programId) is not LiveTvProgram program
                || !ConflictPlanner.IsEpisodeId(program.ShowId))
            {
                _logger.LogInformation("{Name}: already on when the series recording was set up, no episode id to find another airing; recording the rest", t.Name);
                continue;
            }

            byShow ??= AiringsByShow(now.AddMinutes(5), now.AddDays(14));
            var later = byShow.TryGetValue(program.ShowId!, out var list)
                ? list.FirstOrDefault(a => a.ProgramId != programId && a.Start > now.AddMinutes(5))
                : null;
            var title = string.IsNullOrWhiteSpace(program.EpisodeTitle) ? t.Name : t.Name + " - " + _marks.StripMarks(program.EpisodeTitle);
            if (later is null)
            {
                _logger.LogInformation("{Title}: already on when the series recording was set up and not shown again; recording the rest", title);
                continue;
            }

            var record = new ActionRecord
            {
                Time = DateTime.UtcNow,
                Title = title,
                Start = t.StartDate,
                Channel = t.ChannelName ?? string.Empty,
                Action = "moved (already on when the series recording was set up)",
                MovedTo = $"{later.ChannelName} {later.Start:u}",
                ProgramId = programId,
                SeriesTimerId = t.SeriesTimerId
            };
            try
            {
                var laterScheduled = timers.Any(x => Guid.TryParse(x.ProgramId, out var p) && p == later.ProgramId
                    && x.Status is RecordingStatus.New or RecordingStatus.InProgress);
                if (!laterScheduled)
                {
                    await CreateTimerAsync(later.ProgramId, t.PrePaddingSeconds, t.PostPaddingSeconds, ct).ConfigureAwait(false);
                }

                await LiveTv.CancelTimer(t.Id).ConfigureAwait(false);
                // Jellyfin 12 keeps a cancelled series episode's start timer running unless updated.
                await LiveTv.UpdateTimer(t, ct).ConfigureAwait(false);
                _store.SetOutcome(new OutcomeStore.Outcome
                {
                    ProgramId = programId,
                    ShowId = program.ShowId,
                    Title = title,
                    ChannelName = t.ChannelName ?? string.Empty,
                    Start = t.StartDate,
                    End = t.EndDate,
                    Kind = OutcomeStore.KindMoved,
                    Detail = "Recording another airing: " + later.ChannelName + " " + LocalTime(later.Start) + " (it was already on when the series recording was set up)",
                    RecordedInstead = later.ProgramId,
                    SeriesTimerId = t.SeriesTimerId,
                    PrePaddingSeconds = t.PrePaddingSeconds,
                    PostPaddingSeconds = t.PostPaddingSeconds,
                    Time = DateTime.UtcNow
                });
                record.Ok = true;
                _logger.LogInformation("{Title} was already on when the series recording was set up: recording it at {Channel} {Start:u} instead", title, later.ChannelName, later.Start);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                record.Error = ex.Message;
                _logger.LogWarning(ex, "Could not move {Title} to a later airing", title);
            }

            Remember(record);
        }
    }

    // Future airings per show id (the same episode shown again), not already being recorded.
    private Dictionary<string, List<Airing>> AiringsByShow(DateTime from, DateTime to)
    {
        var channels = new Dictionary<Guid, string>();
        string ChannelName(Guid id) => channels.TryGetValue(id, out var n) ? n
            : channels[id] = _libraryManager.GetItemById(id)?.Name ?? string.Empty;

        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.TvProgram },
                MinStartDate = from,
                MaxStartDate = to
            })
            .OfType<LiveTvProgram>()
            .Where(p => !string.IsNullOrWhiteSpace(p.ShowId) && p.EndDate.HasValue)
            .GroupBy(p => p.ShowId!)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => new Airing(p.Id, p.ChannelId, ChannelName(p.ChannelId), p.StartDate, p.EndDate!.Value)).OrderBy(a => a.Start).ToList(),
                StringComparer.Ordinal);
    }

    // Series recordings and favourites ("fav:" + rule id) in the configured priority order; ones not
    // ordered yet go last, by name.
    private static List<SeriesEntry> OrderedEntries(PluginConfiguration cfg, IEnumerable<SeriesTimerInfoDto> all)
    {
        var order = (cfg.SeriesPriority ?? Array.Empty<string>()).Select((id, i) => (id, i))
            .GroupBy(x => x.id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        var entries = all.Select(s => new SeriesEntry(s.Id, s.Name, s.RecordAnyChannel ? "any channel" : s.ChannelName ?? string.Empty))
            .Concat((cfg.Favourites ?? Array.Empty<FavouriteRule>()).Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Title))
                .Select(f => new SeriesEntry(FavouriteKey(f.Id), "★ " + f.Title, "favourite" + (string.IsNullOrWhiteSpace(f.Channels) ? string.Empty : " · " + f.Channels))));
        return entries.OrderBy(e => order.TryGetValue(e.Id, out var i) ? i : int.MaxValue).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string FavouriteKey(string ruleId) => "fav:" + ruleId;

    // JellyDVR Live's buffer timers (read from its shared file; no code dependency).
    private HashSet<string> LiveBufferTimerIds()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var file = Path.Combine(_paths.DataPath, "jellydvr-live", "buffers.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var b in doc.RootElement.EnumerateArray())
                {
                    if (b.TryGetProperty("TimerId", out var id) && id.ValueKind == JsonValueKind.String
                        && !(b.TryGetProperty("Kept", out var kept) && kept.ValueKind == JsonValueKind.True))
                    {
                        result.Add(id.GetString()!);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    private int JellyfinTunerCount()
    {
        var hosts = _config.GetConfiguration<LiveTvOptions>("livetv").TunerHosts ?? Array.Empty<TunerHostInfo>();
        return hosts.Length == 0 || hosts.Any(h => h.TunerCount <= 0) ? 0 : hosts.Sum(h => h.TunerCount);
    }

    // ---- doing it --------------------------------------------------------------------------

    private async Task ExecuteAsync(SchedulePlan plan, CancellationToken ct)
    {
        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;
        // A recording that loses a conflict may move to an airing that was itself dropped as a
        // duplicate: then that airing just keeps its existing recording (Jellyfin reuses timer ids
        // per programme, so cancelling and re-creating it would clash).
        var moveTargets = plan.Decisions.Where(d => d.MoveTo is not null).Select(d => d.MoveTo!.ProgramId).ToHashSet();
        foreach (var d in plan.Decisions.Where(d => d.Kind != DecisionKind.Unresolvable))
        {
            if (d.Kind == DecisionKind.Duplicate && moveTargets.Contains(d.Recording.ProgramId))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            if (d.Kind == DecisionKind.Restore)
            {
                await RestoreAsync(d.Recording, timers, ct).ConfigureAwait(false);
                continue;
            }

            var timer = timers.FirstOrDefault(t => t.Id == d.Recording.TimerId && t.Status == RecordingStatus.New);
            if (timer is null)
            {
                continue; // changed or started since planning
            }

            var record = new ActionRecord
            {
                Time = DateTime.UtcNow,
                Title = d.Recording.Title,
                Start = timer.StartDate,
                Channel = d.Recording.ChannelName,
                Action = d.Kind switch
                {
                    DecisionKind.Move => "moved",
                    DecisionKind.Duplicate => "dropped duplicate",
                    _ => "skipped"
                },
                MovedTo = d.MoveTo is { } m ? $"{m.ChannelName} {m.Start:u}" : null,
                ProgramId = d.Recording.ProgramId,
                SeriesTimerId = d.Recording.SeriesTimerId
            };
            try
            {
                var alreadyScheduled = d.MoveTo is { } target && timers.Any(t =>
                    Guid.TryParse(t.ProgramId, out var p) && p == target.ProgramId
                    && (t.Status == RecordingStatus.New || t.Status == RecordingStatus.InProgress));
                if (d.MoveTo is { } airing && !alreadyScheduled)
                {
                    await _marks.UnmarkAsync(airing.ProgramId, ct).ConfigureAwait(false);
                    var defaults = await LiveTv.GetNewTimerDefaults(airing.ProgramId.ToString("N"), ct).ConfigureAwait(false);
                    await LiveTv.CreateTimer(
                        new TimerInfoDto
                        {
                            ProgramId = airing.ProgramId.ToString("N"),
                            ChannelId = defaults.ChannelId,
                            ServiceName = defaults.ServiceName,
                            Name = defaults.Name,
                            StartDate = defaults.StartDate,
                            EndDate = defaults.EndDate,
                            PrePaddingSeconds = timer.PrePaddingSeconds,
                            PostPaddingSeconds = timer.PostPaddingSeconds,
                            IsPrePaddingRequired = timer.IsPrePaddingRequired,
                            IsPostPaddingRequired = timer.IsPostPaddingRequired,
                            KeepUntil = timer.KeepUntil
                        },
                        ct).ConfigureAwait(false);
                }

                await LiveTv.CancelTimer(timer.Id).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(timer.SeriesTimerId))
                {
                    // Jellyfin 12 keeps a cancelled series episode's start timer running unless
                    // the timer is updated after cancelling.
                    await LiveTv.UpdateTimer(timer, ct).ConfigureAwait(false);
                }

                record.Ok = true;
                _store.SetOutcome(new OutcomeStore.Outcome
                {
                    ProgramId = d.Recording.ProgramId,
                    ShowId = d.Recording.ShowId,
                    Title = d.Recording.Title,
                    ChannelName = d.Recording.ChannelName,
                    Start = timer.StartDate,
                    End = timer.EndDate,
                    Kind = d.Kind switch
                    {
                        DecisionKind.Move => OutcomeStore.KindMoved,
                        DecisionKind.Duplicate => OutcomeStore.KindDuplicate,
                        _ => OutcomeStore.KindSkipped
                    },
                    Detail = d.Kind switch
                    {
                        DecisionKind.Move => "Recording another airing: " + d.MoveTo!.ChannelName + " " + LocalTime(d.MoveTo.Start),
                        DecisionKind.Duplicate => "Recording another airing: " + d.KeptCopy!.ChannelName + " " + LocalTime(d.KeptCopy.Start + d.KeptCopy.PadBefore),
                        _ => "Skipped: tuners busy with " + string.Join(", ", d.Competing.Where(c => c != d.Recording).Select(c => c.Title))
                    },
                    RecordedInstead = d.MoveTo?.ProgramId ?? d.KeptCopy?.ProgramId,
                    SeriesTimerId = timer.SeriesTimerId,
                    PrePaddingSeconds = timer.PrePaddingSeconds,
                    PostPaddingSeconds = timer.PostPaddingSeconds,
                    Time = DateTime.UtcNow
                });
                _logger.LogInformation(
                    "Recording conflict: {Action} {Title} at {Start:u} on {Channel}{To}",
                    record.Action, record.Title, record.Start, record.Channel, record.MovedTo is null ? string.Empty : " → " + record.MovedTo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                record.Error = ex.Message;
                _logger.LogWarning(ex, "Could not resolve the conflict for {Title}", d.Recording.Title);
            }

            Remember(record);
        }
    }

    // Puts a skipped airing back on the schedule. For a series episode Jellyfin re-arms the timer it
    // cancelled (same id, settings kept); a one-off gets a new timer with the saved padding.
    private async Task RestoreAsync(PlannedRecording r, IReadOnlyList<TimerInfoDto> timers, CancellationToken ct)
    {
        var record = new ActionRecord
        {
            Time = DateTime.UtcNow,
            Title = r.Title,
            Start = r.Start + r.PadBefore,
            Channel = r.ChannelName,
            Action = "restored",
            ProgramId = r.ProgramId,
            SeriesTimerId = r.SeriesTimerId
        };
        try
        {
            var scheduled = timers.Any(t => Guid.TryParse(t.ProgramId, out var p) && p == r.ProgramId
                && (t.Status == RecordingStatus.New || t.Status == RecordingStatus.InProgress));
            if (!scheduled)
            {
                await CreateTimerAsync(r.ProgramId, (int)r.PadBefore.TotalSeconds, (int)r.PadAfter.TotalSeconds, ct).ConfigureAwait(false);
            }

            _store.RemoveOutcomes(new[] { r.ProgramId });
            record.Ok = true;
            _logger.LogInformation("Recording conflict cleared: restored {Title} at {Start:u} on {Channel}", r.Title, record.Start, r.ChannelName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Error = ex.Message;
            _logger.LogWarning(ex, "Could not restore {Title}", r.Title);
        }

        Remember(record);
    }

    /// <summary>Schedules one airing (re-arming Jellyfin's cancelled timer for it, if it has one).</summary>
    public async Task CreateTimerAsync(Guid programId, int? prePaddingSeconds, int? postPaddingSeconds, CancellationToken ct)
    {
        // Before Jellyfin copies the title into the timer (and the file name).
        await _marks.UnmarkAsync(programId, ct).ConfigureAwait(false);
        var defaults = await LiveTv.GetNewTimerDefaults(programId.ToString("N"), ct).ConfigureAwait(false);
        await LiveTv.CreateTimer(
            new TimerInfoDto
            {
                ProgramId = programId.ToString("N"),
                ChannelId = defaults.ChannelId,
                ServiceName = defaults.ServiceName,
                Name = defaults.Name,
                StartDate = defaults.StartDate,
                EndDate = defaults.EndDate,
                PrePaddingSeconds = prePaddingSeconds ?? defaults.PrePaddingSeconds,
                PostPaddingSeconds = postPaddingSeconds ?? defaults.PostPaddingSeconds,
                IsPrePaddingRequired = defaults.IsPrePaddingRequired,
                IsPostPaddingRequired = defaults.IsPostPaddingRequired,
                KeepUntil = defaults.KeepUntil
            },
            ct).ConfigureAwait(false);
    }

    private void Remember(ActionRecord record)
    {
        _recent.Enqueue(record);
        while (_recent.Count > 200 && _recent.TryDequeue(out _))
        {
        }
    }

    private static string LocalTime(DateTime utc) => utc.ToLocalTime().ToString("ddd MMM d h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

    public sealed class SchedulePlan
    {
        public DateTime CreatedUtc { get; set; }

        public string Reason { get; set; } = string.Empty;

        public string Mode { get; set; } = PluginConfiguration.ModePreview;

        public int Capacity { get; set; }

        public int Scheduled { get; set; }

        public string? Warning { get; set; }

        public List<SeriesEntry> Series { get; set; } = new();

        public List<Decision> Decisions { get; set; } = new();
    }

    public sealed record SeriesEntry(string Id, string Name, string Channel);

    public sealed class ActionRecord
    {
        public DateTime Time { get; set; }

        public string Title { get; set; } = string.Empty;

        public DateTime Start { get; set; }

        public string Channel { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string? MovedTo { get; set; }

        public Guid ProgramId { get; set; }

        public string? SeriesTimerId { get; set; }

        public bool Ok { get; set; }

        public string? Error { get; set; }
    }
}
