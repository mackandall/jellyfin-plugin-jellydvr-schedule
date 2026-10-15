using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// Jellyfin opens live streams one at a time behind a single server-wide lock with no timeout, so a
/// tuner that accepts a connection and never sends anything (seen 2026-10-05: BroadcastHub froze
/// mid-tune) silently stops every later recording on every tuner. The timer fires, logs nothing more,
/// and stays "New": Jellyfin only marks a timer InProgress once the recorder has actually started.
/// This watches for timers still New well past their start and tells the admins (on screen and in
/// the activity log) so the stuck tuner can be restarted while the programme is still on.
/// </summary>
public sealed class RecordingStartWatchdog : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly ILogger<RecordingStartWatchdog> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly IRecordingsManager _recordings;

    // Timer id + start → when it was reported (forgotten after a day).
    private readonly Dictionary<string, (DateTime Reported, string Title)> _reported = new(StringComparer.Ordinal);

    public RecordingStartWatchdog(
        ILogger<RecordingStartWatchdog> logger,
        IServiceProvider serviceProvider,
        ISessionManager sessionManager,
        IUserManager userManager,
        IRecordingsManager recordings)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _recordings = recordings;
    }

    /// <summary>Recordings that didn't start (and late starts after one), for the settings page.</summary>
    public List<string> Recent { get; } = new();

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private ILiveTvManager LiveTv => _serviceProvider.GetRequiredService<ILiveTvManager>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Recording start check failed");
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
        if (!cfg.StuckRecordingAlert)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var grace = TimeSpan.FromSeconds(Math.Clamp(cfg.StuckRecordingSeconds, 30, 1800));
        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;

        // A stuck one that got going after all (the tuner was restarted): say so once.
        foreach (var t in timers.Where(t => t.Status == RecordingStatus.InProgress))
        {
            foreach (var key in _reported.Keys.Where(k => k.StartsWith(t.Id + "|", StringComparison.Ordinal)).ToList())
            {
                Note($"{_reported[key].Title} started recording after all, {(now - _reported[key].Reported).TotalMinutes:0} min after the alert");
                _reported.Remove(key);
            }
        }

        // Jellyfin's own retry after a failed start moves StartDate a minute ahead, so it isn't caught here.
        var stuck = timers
            .Where(t => t.Status == RecordingStatus.New)
            .Select(t => (Timer: t, Start: t.StartDate.AddSeconds(-Math.Max(0, t.PrePaddingSeconds))))
            .Where(x => now - x.Start >= grace
                && x.Timer.EndDate.AddSeconds(Math.Max(0, x.Timer.PostPaddingSeconds)) > now
                && _recordings.GetActiveRecordingPath(x.Timer.Id) is null)
            .OrderBy(x => x.Start)
            .ToList();

        var fresh = stuck.Where(x => !_reported.ContainsKey(Key(x.Timer, x.Start))).ToList();
        foreach (var (timer, start) in fresh)
        {
            _reported[Key(timer, start)] = (now, timer.Name);
        }

        foreach (var key in _reported.Where(kv => now - kv.Value.Reported > TimeSpan.FromDays(1)).Select(kv => kv.Key).ToList())
        {
            _reported.Remove(key);
        }

        if (fresh.Count == 0)
        {
            return;
        }

        var titles = string.Join(", ", fresh.Select(x => string.Format(
            CultureInfo.InvariantCulture,
            "{0} ({1}, {2:h:mm tt})",
            x.Timer.Name,
            string.IsNullOrEmpty(x.Timer.ChannelName) ? "?" : x.Timer.ChannelName,
            x.Start.ToLocalTime())));
        var text = (fresh.Count == 1 ? "This recording hasn't started: " : "These recordings haven't started: ") + titles
            + ". Jellyfin is probably waiting on a tuner that isn't answering, and that blocks every recording and live channel."
            + " Restarting the stuck tuner app (e.g. BroadcastHub) usually frees it.";
        Note((fresh.Count == 1 ? "Didn't start: " : "Didn't start (" + fresh.Count + "): ") + titles);
        _logger.LogWarning("Recording start watchdog: {Text}", text);

        await LogActivityAsync(fresh.Count == 1 ? "Recording didn't start: " + fresh[0].Timer.Name : fresh.Count + " recordings didn't start", text)
            .ConfigureAwait(false);
        if (cfg.StuckRecordingOnScreen)
        {
            await TellAdminsAsync(text, ct).ConfigureAwait(false);
        }
    }

    private static string Key(TimerInfoDto t, DateTime start) => t.Id + "|" + start.Ticks.ToString(CultureInfo.InvariantCulture);

    internal async Task LogActivityAsync(string name, string overview, string type = "JellyDvrScheduleRecordingNotStarted")
    {
        try
        {
            var activity = _serviceProvider.GetRequiredService<IActivityManager>();
            await activity.CreateAsync(new ActivityLog(name, type, Guid.Empty)
            {
                Overview = overview,
                ShortOverview = "JellyDVR Schedule",
                LogSeverity = LogLevel.Warning
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not add the activity log entry");
        }
    }

    internal async Task TellAdminsAsync(string text, CancellationToken ct, string header = "Recording didn't start")
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-15);
        var sessions = _sessionManager.Sessions
            .Where(s => s.IsActive && s.LastActivityDate > cutoff && !s.UserId.Equals(Guid.Empty))
            .Where(s => _userManager.GetUserById(s.UserId)?.HasPermission(PermissionKind.IsAdministrator) == true)
            .ToList();
        foreach (var s in sessions)
        {
            try
            {
                await _sessionManager.SendMessageCommand(
                    null!,
                    s.Id,
                    new MessageCommand { Header = header, Text = text, TimeoutMs = 60000 },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not show the alert on {Device}", s.DeviceName);
            }
        }
    }

    private void Note(string message)
    {
        lock (Recent)
        {
            Recent.Insert(0, DateTime.Now.ToString("ddd h:mm tt", CultureInfo.InvariantCulture) + " · " + message);
            if (Recent.Count > 50)
            {
                Recent.RemoveAt(Recent.Count - 1);
            }
        }
    }
}
