using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// The moment a recording starts, it needs a tuner input; if people watching live TV hold them all,
/// Jellyfin's recording just fails and retries. Every few seconds this looks at recordings about to
/// start and, per <see cref="PluginConfiguration.LiveViewerPolicy"/>:
/// <list type="bullet">
/// <item>"warn" / "record": picks the viewers whose inputs are needed (fewest viewers on the channel,
/// most recently tuned first), warns them on screen ("warn"), and stops their live TV as the
/// recording starts (a stop command, then closing the stream if the client ignores it).</item>
/// <item>"viewers": re-plans, so the recording moves to another airing or is skipped, and re-plans
/// again when a viewer leaves so a skipped programme still on the air can start late.</item>
/// </list>
/// Viewers on the recording's own channel share its input and are never touched.
/// </summary>
public sealed class LiveViewerGuard : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    /// <summary>Stop viewers this long before the recording (with its padding) starts.</summary>
    private static readonly TimeSpan StopLead = TimeSpan.FromSeconds(20);

    private readonly ILogger<LiveViewerGuard> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISessionManager _sessionManager;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IApplicationPaths _paths;
    private readonly LiveViewers _viewers;
    private readonly ScheduleManager _manager;
    private readonly OutcomeStore _store;

    // (timer id, session id) already warned / stopped, with when (forgotten after a day).
    private readonly Dictionary<string, DateTime> _warned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _stopped = new(StringComparer.Ordinal);
    private string _lastViewerChannels = string.Empty;
    private readonly HashSet<string> _replannedFor = new(StringComparer.Ordinal);

    public LiveViewerGuard(
        ILogger<LiveViewerGuard> logger,
        IServiceProvider serviceProvider,
        ISessionManager sessionManager,
        IMediaSourceManager mediaSources,
        IApplicationPaths paths,
        LiveViewers viewers,
        ScheduleManager manager,
        OutcomeStore store)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sessionManager = sessionManager;
        _mediaSources = mediaSources;
        _paths = paths;
        _viewers = viewers;
        _manager = manager;
        _store = store;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private ILiveTvManager LiveTv => _serviceProvider.GetRequiredService<ILiveTvManager>();

    /// <summary>Recent warnings and stops, for the settings page.</summary>
    public List<string> Recent { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Live viewer check failed");
                }

                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var cfg = Config;
        var capacity = cfg.TunerInputs;
        var viewers = _viewers.Current();
        var now = DateTime.UtcNow;
        Forget(now);

        // "Never interrupt viewers": a viewer leaving may free an input for a skipped programme
        // that is still on (start it late).
        var channelsKey = string.Join(",", viewers.Select(v => v.ChannelId).Distinct().OrderBy(id => id));
        var viewersChanged = channelsKey != _lastViewerChannels;
        _lastViewerChannels = channelsKey;
        if (cfg.Mode != PluginConfiguration.ModeActive || capacity <= 0)
        {
            return;
        }

        if (viewersChanged && cfg.StartLateWhenFree && cfg.RestoreSkipped
            && _store.Outcomes.Any(o => o.Kind == OutcomeStore.KindSkipped && o.Start <= now && o.End > now.AddMinutes(5)))
        {
            await _manager.RunAsync("live TV viewers changed", ct).ConfigureAwait(false);
        }

        if (viewers.Count == 0)
        {
            return;
        }

        var policy = cfg.LiveViewerPolicy;
        var lead = policy == PluginConfiguration.ViewersWarn
            ? TimeSpan.FromMinutes(Math.Clamp(cfg.WarnMinutes, 0, 30)) + StopLead
            : policy == PluginConfiguration.ViewersFirst ? ScheduleManager.ViewerHold - TimeSpan.FromMinutes(1) : StopLead + Tick;

        var buffers = LiveBufferTimerIds();
        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items
            .Where(t => !buffers.Contains(t.Id))
            .ToList();
        var due = timers
            .Where(t => t.Status == RecordingStatus.New)
            .Select(t => (Timer: t, Start: t.StartDate.AddSeconds(-Math.Max(0, t.PrePaddingSeconds))))
            .Where(x => x.Start <= now + lead && x.Timer.EndDate > now)
            .OrderBy(x => x.Start)
            .ToList();
        if (due.Count == 0)
        {
            return;
        }

        foreach (var (timer, start) in due)
        {
            // Inputs taken when this recording starts: recordings still running then, the ones due
            // before it, its own channel, and the channels people are watching.
            var recordingChannels = timers
                .Where(t => t.Status == RecordingStatus.InProgress && t.EndDate.AddSeconds(Math.Max(0, t.PostPaddingSeconds)) > start)
                .Select(t => t.ChannelId)
                .Concat(due.TakeWhile(d => d.Timer != timer).Select(d => d.Timer.ChannelId))
                .Append(timer.ChannelId)
                .ToHashSet();
            if (recordingChannels.Count > capacity)
            {
                continue; // recordings alone don't fit: the conflict planner's job
            }

            var viewerChannels = viewers.Select(v => v.ChannelId).Where(id => !recordingChannels.Contains(id)).Distinct().ToList();
            var excess = recordingChannels.Count + viewerChannels.Count - capacity;
            if (excess <= 0)
            {
                continue;
            }

            if (policy == PluginConfiguration.ViewersFirst)
            {
                if (_replannedFor.Add(timer.Id))
                {
                    _logger.LogInformation("{Title} starts at {Start:t} but live TV holds the tuners: re-planning (viewers first)", timer.Name, start.ToLocalTime());
                    await _manager.RunAsync("live TV viewers", ct).ConfigureAwait(false);
                }

                continue;
            }

            // Who gives way: channels with the fewest viewers, then the most recently tuned.
            var victims = viewers
                .Where(v => viewerChannels.Contains(v.ChannelId))
                .GroupBy(v => v.ChannelId)
                .OrderBy(g => g.Count())
                .ThenByDescending(g => g.Max(v => v.TunedInUtc))
                .Take(excess)
                .SelectMany(g => g)
                .ToList();
            foreach (var v in victims)
            {
                var key = timer.Id + "|" + v.SessionId;
                if (policy == PluginConfiguration.ViewersWarn && !_warned.ContainsKey(key) && now < start - StopLead)
                {
                    _warned[key] = now;
                    await WarnAsync(v, timer, start, ct).ConfigureAwait(false);
                }

                if (now >= start - StopLead && !_stopped.ContainsKey(key))
                {
                    _stopped[key] = now;
                    await StopAsync(v, timer, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task WarnAsync(LiveViewer v, TimerInfoDto timer, DateTime start, CancellationToken ct)
    {
        var text = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} starts recording at {1:h:mm tt} and needs a tuner. Live TV on {2} will stop then.",
            Title(timer),
            timer.StartDate.ToLocalTime(),
            v.ChannelName);
        try
        {
            await _sessionManager.SendMessageCommand(
                null!,
                v.SessionId,
                new MessageCommand { Header = "Recording about to start", Text = text, TimeoutMs = 20000 },
                ct).ConfigureAwait(false);
            Note($"Warned {v.User} ({v.Describe()}): {Title(timer)} at {timer.StartDate.ToLocalTime():t}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not warn {Device}", v.Device);
        }
    }

    private async Task StopAsync(LiveViewer v, TimerInfoDto timer, CancellationToken ct)
    {
        try
        {
            await _sessionManager.SendMessageCommand(
                null!,
                v.SessionId,
                new MessageCommand { Header = "Live TV stopped", Text = "Recording " + Title(timer) + " needs this tuner.", TimeoutMs = 15000 },
                ct).ConfigureAwait(false);
            await _sessionManager.SendPlaystateCommand(null!, v.SessionId, new PlaystateRequest { Command = PlaystateCommand.Stop }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not send stop to {Device}", v.Device);
        }

        Note($"Stopped live TV ({v.Describe()}, {v.User}) for {Title(timer)}");

        // A client that ignores remote control keeps the stream (and the input): close it.
        _ = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(v.LiveStreamId)
                    && _viewers.Current().Any(x => x.SessionId == v.SessionId && x.ChannelId == v.ChannelId))
                {
                    try
                    {
                        await _mediaSources.CloseLiveStream(v.LiveStreamId).ConfigureAwait(false);
                        Note($"Closed the live stream of {v.Describe()} (the app didn't stop)");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Could not close the live stream of {Device}", v.Device);
                    }
                }
            },
            ct);
    }

    private static string Title(TimerInfoDto t) => t.Name;

    private void Note(string message)
    {
        _logger.LogInformation("Live TV vs recording: {Message}", message);
        lock (Recent)
        {
            Recent.Insert(0, DateTime.Now.ToString("ddd h:mm tt", System.Globalization.CultureInfo.InvariantCulture) + " · " + message);
            if (Recent.Count > 50)
            {
                Recent.RemoveAt(Recent.Count - 1);
            }
        }
    }

    private void Forget(DateTime now)
    {
        foreach (var d in new[] { _warned, _stopped })
        {
            foreach (var k in d.Where(kv => now - kv.Value > TimeSpan.FromDays(1)).Select(kv => kv.Key).ToList())
            {
                d.Remove(k);
            }
        }

        if (_replannedFor.Count > 500)
        {
            _replannedFor.Clear();
        }
    }

    // JellyDVR Live's unkept buffer timers: they share a viewer's stream and give way on their own.
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
}
