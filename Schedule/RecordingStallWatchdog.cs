using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// A tuner can go quiet in the middle of a recording (seen 2026-10-13: BroadcastHub's capture
/// stopped 69 minutes into a film). Jellyfin's recorder then waits for more data until the
/// programme ends: the recording "runs" its full length and the file holds only the start. This
/// watches each recording's file; one that stops growing is reported to the admins and, unless
/// turned off, tuned again: the stalled recording is stopped and the same airing scheduled anew,
/// which Jellyfin starts at once as a second file. Live TV buffers are left alone.
/// </summary>
public sealed class RecordingStallWatchdog : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    // Not worth a new file for less than this of the programme.
    private static readonly TimeSpan MinLeft = TimeSpan.FromMinutes(2);

    private readonly ILogger<RecordingStallWatchdog> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRecordingsManager _recordings;
    private readonly ActiveRecordings _active;
    private readonly ScheduleManager _manager;
    private readonly RecordingStartWatchdog _alerts;

    // By the DVR's timer id: the file's size when it last grew.
    private readonly Dictionary<string, Watch> _watched = new(StringComparer.Ordinal);

    // By programme (guide entry id): how often it was tuned again, and when last.
    private readonly Dictionary<string, (int Count, DateTime At)> _retunes = new(StringComparer.Ordinal);
    private readonly List<string> _recent = new();

    public RecordingStallWatchdog(
        ILogger<RecordingStallWatchdog> logger,
        IServiceProvider serviceProvider,
        IRecordingsManager recordings,
        ActiveRecordings active,
        ScheduleManager manager,
        RecordingStartWatchdog alerts)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _recordings = recordings;
        _active = active;
        _manager = manager;
        _alerts = alerts;
    }

    private sealed class Watch
    {
        public long Length { get; set; }

        public DateTime GrewUtc { get; set; }

        public bool Handled { get; set; }
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private ILiveTvManager LiveTv => _serviceProvider.GetRequiredService<ILiveTvManager>();

    /// <summary>Stalls since Jellyfin started, newest first, for the settings page.</summary>
    public List<string> RecentNotes()
    {
        lock (_recent)
        {
            return _recent.ToList();
        }
    }

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
                    _logger.LogError(ex, "Recording stall check failed");
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
        if (!cfg.StallWatch)
        {
            _watched.Clear();
            return;
        }

        var now = DateTime.UtcNow;
        var limit = TimeSpan.FromSeconds(Math.Clamp(cfg.StallSeconds, 30, 900));
        var buffers = _manager.LiveBufferTimerIds();
        var active = _active.Current().Where(r => !buffers.Contains(r.TimerId)).ToList();

        foreach (var gone in _watched.Keys.Where(id => active.All(r => r.TimerId != id)).ToList())
        {
            _watched.Remove(gone);
        }

        foreach (var key in _retunes.Where(kv => now - kv.Value.At > TimeSpan.FromHours(12)).Select(kv => kv.Key).ToList())
        {
            _retunes.Remove(key);
        }

        foreach (var rec in active)
        {
            var length = Length(_recordings.GetActiveRecordingPath(rec.TimerId));
            if (!_watched.TryGetValue(rec.TimerId, out var watch))
            {
                _watched[rec.TimerId] = new Watch { Length = length, GrewUtc = now };
                continue;
            }

            if (length > watch.Length)
            {
                if (watch.Handled)
                {
                    Note($"{rec.Name} is receiving data again");
                }

                watch.Length = length;
                watch.GrewUtc = now;
                watch.Handled = false;
                continue;
            }

            if (watch.Handled || now - watch.GrewUtc < limit)
            {
                continue;
            }

            watch.Handled = true;
            await HandleAsync(rec, watch.GrewUtc, cfg, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(ActiveRecording rec, DateTime sinceUtc, PluginConfiguration cfg, CancellationToken ct)
    {
        var since = sinceUtc.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);
        var what = $"{rec.Name} stopped receiving data at {since}";
        string outcome;
        try
        {
            outcome = cfg.StallRetune ? await RetuneAsync(rec, cfg, ct).ConfigureAwait(false) : "Tuning again is turned off, so nothing more is being recorded.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not tune {Name} again", rec.Name);
            outcome = "Tuning it again failed (" + ex.Message + ").";
        }

        var text = "The recording of " + what + ": the tuner went quiet. " + outcome;
        Note(what + ". " + outcome);
        _logger.LogWarning("Recording stall watchdog: {Text}", text);
        await _alerts.LogActivityAsync("Recording stalled: " + rec.Name, text, "JellyDvrScheduleRecordingStalled").ConfigureAwait(false);
        if (cfg.StuckRecordingOnScreen)
        {
            await _alerts.TellAdminsAsync(text, ct, "Recording stalled").ConfigureAwait(false);
        }
    }

    /// <summary>Stops the stalled recording and schedules the same airing again; says what was done.</summary>
    private async Task<string> RetuneAsync(ActiveRecording rec, PluginConfiguration cfg, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;

        // GetTimers knows a timer by its API id; the recorder by the DVR's own.
        var timer = timers.FirstOrDefault(t => string.Equals(t.ExternalId, rec.TimerId, StringComparison.Ordinal));
        if (timer is null || !Guid.TryParse(timer.ProgramId, out var programId))
        {
            return "It has no guide entry to schedule again, so it was left as it is.";
        }

        if (timer.EndDate - now < MinLeft)
        {
            return "The programme is about over, so it was left as it is.";
        }

        var max = Math.Clamp(cfg.StallMaxRetunes, 1, 10);
        var done = _retunes.TryGetValue(timer.ProgramId, out var r) ? r.Count : 0;
        if (done >= max)
        {
            return $"It was already tuned again {done} time(s), so it was left as it is.";
        }

        _retunes[timer.ProgramId] = (done + 1, now);
        await LiveTv.CancelTimer(timer.Id).ConfigureAwait(false);

        // The new recording must not start while the old one still holds the file name and the tuner.
        for (var i = 0; i < 40 && _recordings.GetActiveRecordingPath(rec.TimerId) is not null; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        await _manager.CreateTimerAsync(programId, 0, timer.PostPaddingSeconds, ct).ConfigureAwait(false);
        return "It was stopped and the rest of the programme is being recorded as a new file"
            + (done > 0 ? $" (tuned again {done + 1} times)." : ".");
    }

    private static long Length(string? path)
    {
        try
        {
            return string.IsNullOrEmpty(path) ? 0 : new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void Note(string message)
    {
        lock (_recent)
        {
            _recent.Insert(0, DateTime.Now.ToString("ddd h:mm tt", CultureInfo.InvariantCulture) + " · " + message);
            if (_recent.Count > 50)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }
        }
    }
}
