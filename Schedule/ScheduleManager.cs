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

    private readonly ILogger<ScheduleManager> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IConfigurationManager _config;
    private readonly IApplicationPaths _paths;
    private readonly IServiceProvider _serviceProvider;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ConcurrentQueue<ActionRecord> _recent = new();
    private Timer? _soon;
    private CancellationToken _stopping;

    public ScheduleManager(
        ILogger<ScheduleManager> logger,
        ILibraryManager libraryManager,
        IConfigurationManager config,
        IApplicationPaths paths,
        IServiceProvider serviceProvider)
    {
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

    // ---- scheduling ------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _soon = new Timer(_ => _ = RunSafeAsync("recordings changed"), null, Timeout.Infinite, Timeout.Infinite);
        LiveTv.TimerCreated += OnScheduleChanged;
        LiveTv.SeriesTimerCreated += OnScheduleChanged;
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
            LiveTv.TimerCancelled -= OnScheduleChanged;
            _soon.Dispose();
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
            var plan = await BuildPlanAsync(cfg, ct).ConfigureAwait(false);
            plan.Reason = reason;
            LastPlan = plan;
            var toDo = plan.Decisions.Count(d => d.Kind != DecisionKind.Unresolvable);
            if (toDo > 0)
            {
                _logger.LogInformation(
                    "Recording conflicts ({Reason}): {Count} recording(s) to move or skip{Mode}",
                    reason, toDo, cfg.Mode == PluginConfiguration.ModeActive ? string.Empty : " (preview: nothing done)");
            }

            if (cfg.Mode == PluginConfiguration.ModeActive && toDo > 0)
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

    // ---- planning --------------------------------------------------------------------------

    private async Task<SchedulePlan> BuildPlanAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var horizon = now.AddDays(Math.Clamp(cfg.LookAheadDays, 1, 14));
        var capacity = cfg.TunerInputs > 0 ? cfg.TunerInputs : JellyfinTunerCount();
        var plan = new SchedulePlan { CreatedUtc = now, Mode = cfg.Mode, Capacity = capacity };

        var seriesTimers = (await LiveTv.GetSeriesTimers(new SeriesTimerQuery(), ct).ConfigureAwait(false)).Items;
        plan.Series = OrderedSeries(cfg, seriesTimers).Select(s => new SeriesEntry(s.Id, s.Name, s.RecordAnyChannel ? "any channel" : s.ChannelName ?? string.Empty)).ToList();
        if (capacity <= 0)
        {
            plan.Warning = "The tuner count is unlimited in Jellyfin; set \"Tuner inputs\" so conflicts can be found.";
            return plan;
        }

        var buffers = LiveBufferTimerIds();
        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items
            .Where(t => (t.Status == RecordingStatus.New && t.StartDate < horizon && t.EndDate > now) || t.Status == RecordingStatus.InProgress)
            .Where(t => !buffers.Contains(t.Id)) // JellyDVR Live buffers give way on their own
            .ToList();

        var seriesRank = plan.Series.Select((s, i) => (s.Id, Rank: 10000 - i)).ToDictionary(x => x.Id, x => x.Rank, StringComparer.Ordinal);
        var recordings = timers.Select(t => ToPlanned(t, cfg, seriesRank)).ToList();
        plan.Scheduled = recordings.Count;

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

        plan.Decisions = ConflictPlanner.Plan(recordings, capacity, cfg.MoveToOtherAiring, Alternatives);
        return plan;
    }

    private PlannedRecording ToPlanned(TimerInfoDto t, PluginConfiguration cfg, Dictionary<string, int> seriesRank)
    {
        var program = Guid.TryParse(t.ProgramId, out var pid) ? _libraryManager.GetItemById(pid) as LiveTvProgram : null;
        var rank = string.IsNullOrEmpty(t.SeriesTimerId)
            ? (cfg.OneOffAboveSeries ? 100000 : 5000)
            : seriesRank.TryGetValue(t.SeriesTimerId!, out var r) ? r : 1;
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
            after);
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

    private static IEnumerable<SeriesTimerInfoDto> OrderedSeries(PluginConfiguration cfg, IEnumerable<SeriesTimerInfoDto> all)
    {
        var order = (cfg.SeriesPriority ?? Array.Empty<string>()).Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
        return all.OrderBy(s => order.TryGetValue(s.Id, out var i) ? i : int.MaxValue).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

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
        foreach (var d in plan.Decisions.Where(d => d.Kind != DecisionKind.Unresolvable))
        {
            ct.ThrowIfCancellationRequested();
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
                Action = d.Kind == DecisionKind.Move ? "moved" : "skipped",
                MovedTo = d.MoveTo is { } m ? $"{m.ChannelName} {m.Start:u}" : null,
                ProgramId = d.Recording.ProgramId,
                SeriesTimerId = d.Recording.SeriesTimerId
            };
            try
            {
                if (d.MoveTo is { } airing)
                {
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
                _logger.LogInformation(
                    "Recording conflict: {Action} {Title} at {Start:u} on {Channel}{To}",
                    record.Action, record.Title, record.Start, record.Channel, record.MovedTo is null ? string.Empty : " → " + record.MovedTo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                record.Error = ex.Message;
                _logger.LogWarning(ex, "Could not resolve the conflict for {Title}", d.Recording.Title);
            }

            _recent.Enqueue(record);
            while (_recent.Count > 200 && _recent.TryDequeue(out _))
            {
            }
        }
    }

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
