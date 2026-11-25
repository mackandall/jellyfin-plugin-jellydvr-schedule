using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Plugin.DvrSchedule.Alerts;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// Makes sure a recording interrupted by a restart, a crash, a kill or a power cut records the rest
/// of its programme. Each recording is written to a log on disk shortly before it starts, with its
/// end time, and stays there until the programme is over or someone stops or cancels it. After any
/// start of Jellyfin, every logged recording whose programme is still on is checked for a few
/// minutes:
/// <list type="bullet">
/// <item>recording, or Jellyfin still starting / retrying it (timer New or InProgress): left alone;</item>
/// <item>Jellyfin gave up on it: a clean shutdown marks it Completed (seen 2026-11-18, Perry Mason
/// kept 3½ of 65 minutes), a failed start may end Error or be deleted. It is scheduled again;
/// Jellyfin re-arms the same timer (or makes a new one) and starts it at once as a second file,
/// which Episode History joins onto the first when the programme ends;</item>
/// <item>cancelled: someone stopped it on purpose, so it stays stopped.</item>
/// <item>stopped before Jellyfin went down: not interrupted by the restart, so left stopped. A stop is
/// kept in the log until the programme is over (Jellyfin marks a stopped recording Completed, the same
/// as one a shutdown cut off, and the recorder winds down for a few seconds after the stop, so the
/// log can't tell the two apart from the timer alone). As a second check, the log notes when each
/// recording was last seen recording: one that had already stopped while Jellyfin was still running
/// is never started again (seen 2026-11-21: FBI: Most Wanted, stopped by hand at 1:22 AM, was
/// started again after a restart at 1:26).</item>
/// </list>
/// A programme that ended while Jellyfin was down is left to Episode History, which reports the
/// recording as short and, with its "Record again automatically" setting, records the next airing.
/// Live TV buffers are left alone.
/// </summary>
public sealed class RestartResume : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    // A recording is logged this long before its (padded) start, so a power cut right at the start is covered.
    private static readonly TimeSpan NoteAhead = TimeSpan.FromMinutes(2);

    // Not worth a new file for less than this of the programme.
    private static readonly TimeSpan MinLeft = TimeSpan.FromMinutes(2);

    // After a power cut the tuners (BroadcastHub, the HDHomeRun) may come up after Jellyfin: keep
    // checking for this long after startup.
    private static readonly TimeSpan CheckFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

    // How often a logged recording's "last seen" times are written down.
    private static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(1);

    // Last seen recording this long before the log's last look: it had stopped while Jellyfin was up.
    private static readonly TimeSpan StoppedBefore = TimeSpan.FromMinutes(2);

    // Recording again this long after a stop: started again on purpose, so watched again.
    private static readonly TimeSpan StopSettles = TimeSpan.FromMinutes(1);

    // Most times one recording is scheduled again after one start of Jellyfin.
    private const int MaxResumes = 3;

    // Jellyfin's "Clean Transcode Directory" runs at startup and would delete a new recording's temp
    // file (seen 2026-10-14), so the first resume waits for it, but not longer than this.
    private static readonly TimeSpan CleanWaitMax = TimeSpan.FromSeconds(90);

    private readonly ILogger<RestartResume> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IApplicationPaths _paths;
    private readonly IRecordingsManager _recordings;
    private readonly ScheduleManager _manager;
    private readonly RecordingStartWatchdog _alerts;
    private readonly AlertCenter _center;
    private readonly object _lock = new();
    private readonly List<string> _recent = new();

    // Recordings still being checked after this start of Jellyfin: their "last seen" times stay as
    // they were before the restart (they aren't recording because of it).
    private readonly HashSet<string> _resuming = new(StringComparer.Ordinal);
    private Dictionary<string, Entry> _log = new(StringComparer.Ordinal);

    public RestartResume(
        ILogger<RestartResume> logger,
        IServiceProvider serviceProvider,
        IApplicationPaths paths,
        IRecordingsManager recordings,
        ScheduleManager manager,
        RecordingStartWatchdog alerts,
        AlertCenter center)
    {
        _center = center;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _paths = paths;
        _recordings = recordings;
        _manager = manager;
        _alerts = alerts;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private ILiveTvManager LiveTv => _serviceProvider.GetRequiredService<ILiveTvManager>();

    private string LogFile => Path.Combine(_paths.DataPath, "jellydvr-schedule", "recording-log.json");

    /// <summary>Resumes since Jellyfin started, newest first, for the settings page.</summary>
    public List<string> RecentNotes()
    {
        lock (_recent)
        {
            return _recent.ToList();
        }
    }

    public sealed class Entry
    {
        /// <summary>The DVR's own timer id (timers.json).</summary>
        public string TimerId { get; set; } = string.Empty;

        /// <summary>The guide entry's library id.</summary>
        public string ProgramId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Channel { get; set; } = string.Empty;

        /// <summary>When the recording starts and ends, padding included.</summary>
        public DateTime StartUtc { get; set; }

        public DateTime EndUtc { get; set; }

        public DateTime NotedUtc { get; set; }

        /// <summary>The file being recorded when last seen (to tell how much a restart cost).</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>When someone stopped or cancelled it (kept until the programme is over, so it stays stopped).</summary>
        public DateTime? StoppedUtc { get; set; }

        /// <summary>The last look at it (about once a minute while Jellyfin runs).</summary>
        public DateTime? CheckedUtc { get; set; }

        /// <summary>When it was last seen recording, at that look.</summary>
        public DateTime? RecordingUtc { get; set; }

        /// <summary>
        /// It had stopped recording while Jellyfin was still running: seen recording, then seen not
        /// recording a while later, so a restart didn't cut it off.
        /// </summary>
        public bool StoppedBeforeShutdown(TimeSpan grace) =>
            RecordingUtc is { } rec && CheckedUtc is { } chk && chk - rec > grace;
    }

    private enum Outcome
    {
        Waiting,
        Recording,
        Stopped,
        Over,
        GaveUp
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // What was logged before this start of Jellyfin.
        Dictionary<string, Entry> before;
        lock (_lock)
        {
            _log = Load();
            // Copies: the log's own entries keep changing while the check runs.
            before = _log.ToDictionary(kv => kv.Key, kv => Copy(kv.Value), StringComparer.Ordinal);
            var now = DateTime.UtcNow;
            foreach (var e in before.Values.Where(e => e.StartUtc <= now && e.EndUtc > now))
            {
                _resuming.Add(e.TimerId);
            }
        }

        LiveTv.TimerCancelled += OnTimerCancelled;
        try
        {
            var check = Task.Run(() => CheckAfterStartAsync(before, stoppingToken), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await NoteAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Could not log the coming recordings");
                }

                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }

            await check.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            LiveTv.TimerCancelled -= OnTimerCancelled;
        }
    }

    // Stopped or cancelled by someone (or by this plugin): out of the log at once, so a deliberate stop
    // is never undone, even if the power goes a second later.
    private void OnTimerCancelled(object? sender, GenericEventArgs<TimerEventInfo> e)
    {
        if (e.Argument?.Id is not { Length: > 0 } id)
        {
            return;
        }

        lock (_lock)
        {
            if (_log.TryGetValue(id, out var entry) && entry.StoppedUtc is null)
            {
                entry.StoppedUtc = DateTime.UtcNow;
                Save();
            }
        }
    }

    private async Task NoteAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var buffers = _manager.LiveBufferTimerIds();
        var timers = (await TimerReader.AllAsync(_serviceProvider, ct).ConfigureAwait(false));
        lock (_lock)
        {
            var changed = false;
            foreach (var t in timers.Where(t => t.Status is RecordingStatus.New or RecordingStatus.InProgress))
            {
                // GetTimers knows a timer by its API id; the recorder and the cancel event by the DVR's own.
                var id = string.IsNullOrEmpty(t.ExternalId) ? t.Id : t.ExternalId;
                var start = t.StartDate.ToUniversalTime().AddSeconds(-Math.Max(0, t.PrePaddingSeconds));
                var end = t.EndDate.ToUniversalTime().AddSeconds(Math.Max(0, t.PostPaddingSeconds));
                if (buffers.Contains(id) || buffers.Contains(t.Id) || string.IsNullOrEmpty(t.ProgramId)
                    || start > now + NoteAhead || end <= now)
                {
                    continue;
                }

                var path = _recordings.GetActiveRecordingPath(id) ?? string.Empty;
                if (_log.TryGetValue(id, out var e) && e.StoppedUtc is { } stopped)
                {
                    // Stopped on purpose: stays out, even while the recorder winds down, unless it
                    // records a new file well after the stop (recorded again on purpose).
                    if (path.Length == 0 || path == e.Path || now - stopped < StopSettles)
                    {
                        continue;
                    }

                    e = null;
                }

                if (e is not null && e.EndUtc == end && e.StartUtc == start && (path.Length == 0 || path == e.Path))
                {
                    continue;
                }

                _log[id] = new Entry
                {
                    TimerId = id,
                    ProgramId = t.ProgramId,
                    Name = t.Name ?? string.Empty,
                    Channel = t.ChannelName ?? string.Empty,
                    StartUtc = start,
                    EndUtc = end, // follows extensions (sports overruns)
                    NotedUtc = e?.NotedUtc ?? now,
                    Path = path.Length > 0 ? path : e?.Path ?? string.Empty,
                    CheckedUtc = e?.CheckedUtc,
                    RecordingUtc = e?.RecordingUtc
                };
                changed = true;
            }

            // Last seen: when each recording on the log was last looked at, and last seen recording.
            foreach (var e in _log.Values.Where(x => x.StoppedUtc is null && x.StartUtc <= now && x.EndUtc > now && !_resuming.Contains(x.TimerId)))
            {
                if (e.CheckedUtc is { } at && now - at < SeenEvery)
                {
                    continue;
                }

                e.CheckedUtc = now;
                if (_recordings.GetActiveRecordingPath(e.TimerId) is not null)
                {
                    e.RecordingUtc = now;
                }

                changed = true;
            }

            // Over for an hour: forgotten.
            foreach (var old in _log.Values.Where(x => x.EndUtc < now - TimeSpan.FromHours(1)).Select(x => x.TimerId).ToList())
            {
                _log.Remove(old);
                changed = true;
            }

            if (changed)
            {
                Save();
            }
        }
    }

    private async Task CheckAfterStartAsync(Dictionary<string, Entry> before, CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var e in before.Values.Where(e => e.StartUtc <= now && e.EndUtc > now && e.StoppedUtc is null && e.StoppedBeforeShutdown(StoppedBefore)))
            {
                Note($"{e.Name}: had stopped before Jellyfin went down (last recording {e.RecordingUtc!.Value.ToLocalTime():h:mm tt}); left stopped");
                _logger.LogInformation(
                    "Restart resume: {Name} had stopped recording at {Recording:u}, before Jellyfin went down (still running at {Checked:u}); left stopped",
                    e.Name, e.RecordingUtc, e.CheckedUtc);
            }

            var pending = before.Values.Where(e => e.StartUtc <= now && e.EndUtc > now && e.StoppedUtc is null && !e.StoppedBeforeShutdown(StoppedBefore)).ToList();
            foreach (var e in before.Values.Where(e => e.StartUtc <= now && e.EndUtc <= now && e.EndUtc > now - TimeSpan.FromDays(1) && e.StoppedUtc is null))
            {
                Note($"{e.Name}: the programme ended while Jellyfin was down; Episode History reports it if it came out short");
                _logger.LogInformation("Restart resume: {Name} ended at {End:u} while Jellyfin was down; left to Episode History", e.Name, e.EndUtc);
            }

            if (pending.Count == 0)
            {
                return;
            }

            if (!Config.ResumeAfterRestart)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                foreach (var e in pending.Where(e => _recordings.GetActiveRecordingPath(e.TimerId) is null && IsLogged(e)))
                {
                    await TellFailedAsync(e, "Recording the rest after a restart is turned off (JellyDVR Schedule › Alerts).", ct).ConfigureAwait(false);
                }

                return;
            }

            await WaitForCleanupAsync(ct).ConfigureAwait(false);
            var resumes = new Dictionary<string, int>(StringComparer.Ordinal);
            var until = DateTime.UtcNow + CheckFor;
            while (pending.Count > 0)
            {
                var last = DateTime.UtcNow >= until;
                var states = TimerStates();
                foreach (var e in pending.ToList())
                {
                    var outcome = await CheckOneAsync(e, states, resumes, ct).ConfigureAwait(false);
                    switch (outcome)
                    {
                        case Outcome.Recording:
                            await TellResumedAsync(e, resumes.ContainsKey(e.TimerId), ct).ConfigureAwait(false);
                            pending.Remove(e);
                            break;
                        case Outcome.GaveUp:
                            await TellFailedAsync(e, $"It was scheduled again {resumes.GetValueOrDefault(e.TimerId)} times and still didn't record. The tuner may not be answering.", ct).ConfigureAwait(false);
                            pending.Remove(e);
                            break;
                        case Outcome.Stopped:
                        case Outcome.Over:
                            pending.Remove(e); // a stop by hand, or Episode History's "came out short" covers it
                            break;
                        case Outcome.Waiting when last:
                            await TellFailedAsync(e, $"It still isn't recording {CheckFor.TotalMinutes:0} minutes after Jellyfin started. The tuner may not be answering.", ct).ConfigureAwait(false);
                            pending.Remove(e);
                            break;
                    }
                }

                if (pending.Count == 0 || last)
                {
                    break;
                }

                await Task.Delay(CheckEvery, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Resuming recordings after the restart failed");
        }
        finally
        {
            lock (_lock)
            {
                _resuming.Clear();
            }
        }
    }

    private static Entry Copy(Entry e) => new()
    {
        TimerId = e.TimerId,
        ProgramId = e.ProgramId,
        Name = e.Name,
        Channel = e.Channel,
        StartUtc = e.StartUtc,
        EndUtc = e.EndUtc,
        NotedUtc = e.NotedUtc,
        Path = e.Path,
        StoppedUtc = e.StoppedUtc,
        CheckedUtc = e.CheckedUtc,
        RecordingUtc = e.RecordingUtc
    };

    // On the log and not stopped by someone since.
    private bool IsLogged(Entry e)
    {
        lock (_lock)
        {
            return _log.TryGetValue(e.TimerId, out var now) && now.StoppedUtc is null;
        }
    }

    /// <summary>One logged recording after startup: where it stands, scheduling it again when Jellyfin gave up on it.</summary>
    private async Task<Outcome> CheckOneAsync(Entry e, Dictionary<string, string> states, Dictionary<string, int> resumes, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (!IsLogged(e))
        {
            return Outcome.Stopped; // stopped or cancelled by someone since
        }

        if (_recordings.GetActiveRecordingPath(e.TimerId) is not null)
        {
            return Outcome.Recording; // Jellyfin resumed it, or we did
        }

        if (e.EndUtc - now < MinLeft)
        {
            return Outcome.Over;
        }

        var status = states.TryGetValue(e.TimerId, out var s) ? s : "gone";
        if (status is "Cancelled")
        {
            return Outcome.Stopped;
        }

        if (status is "New" or "InProgress")
        {
            return Outcome.Waiting; // Jellyfin is starting or retrying it
        }

        // Completed (clean shutdown), Error, or the timer is gone: Jellyfin gave up on it.
        if (!Guid.TryParse(e.ProgramId, out var programId))
        {
            return Outcome.GaveUp;
        }

        var done = resumes.TryGetValue(e.TimerId, out var n) ? n : 0;
        if (done >= MaxResumes)
        {
            Note($"{e.Name}: scheduled again {done} times since the restart and still not recording; given up");
            return Outcome.GaveUp;
        }

        resumes[e.TimerId] = done + 1;
        try
        {
            await _manager.CreateTimerAsync(programId, null, null, ct).ConfigureAwait(false);
            var minutes = (e.EndUtc - now).TotalMinutes;
            var why = status switch
            {
                "Completed" => "Jellyfin stopped it when it shut down",
                "gone" => "Jellyfin dropped its timer",
                _ => "Jellyfin gave up on it (" + status + ")"
            };
            var text = $"{e.Name}{(string.IsNullOrEmpty(e.Channel) ? string.Empty : " (" + e.Channel + ")")} was interrupted: {why}. Recording the remaining {minutes:0} min as a second file, which Episode History joins onto the first when the programme ends.";
            _logger.LogInformation("Restart resume: {Text}", text);
            Note($"{e.Name}: resumed after the restart ({minutes:0} min left; {why})");
            await _alerts.LogActivityAsync("Recording resumed after restart: " + e.Name, text, "JellyDvrScheduleRecordingResumed").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not resume {Name} after the restart", e.Name);
            Note($"{e.Name}: couldn't be resumed after the restart ({ex.Message})");
        }

        // Jellyfin starts it within a second or two; the recorder shows up by the next look (or not).
        await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        return _recordings.GetActiveRecordingPath(e.TimerId) is not null ? Outcome.Recording : Outcome.Waiting;
    }

    private static string AlertKey(Entry e) => "restart:" + e.TimerId + "|" + e.StartUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private AlertReport Report(Entry e, string text) => new()
    {
        Kind = AlertKinds.Short,
        Key = AlertKey(e),
        Title = e.Name + " was interrupted by a Jellyfin restart",
        Text = text,
        Where = string.IsNullOrEmpty(e.Channel) ? null : e.Channel,
        AiredUtc = e.StartUtc,
        ProgramId = Guid.TryParse(e.ProgramId, out var pid) ? pid : null,
        Source = "JellyDVR Schedule"
    };

    // Recording again: listed as a problem that fixed itself, sent nowhere (no pop-up, phone or email).
    private async Task TellResumedAsync(Entry e, bool byUs, CancellationToken ct)
    {
        var lost = Lost(e);
        var who = byUs ? "JellyDVR Schedule started it again" : "Jellyfin picked it up again itself";
        await _center.RaiseAsync(Report(e, "The recording stopped when Jellyfin went down."), ct, send: false).ConfigureAwait(false);
        await _center.ResolveAsync(
            AlertKey(e),
            (lost is { } l ? $"Recording again after {Describe(l)}" : "Recording again") + $"; {who}. The pieces are joined when the programme ends.",
            true,
            ct).ConfigureAwait(false);
    }

    // Not recording again: an open problem, sent like the other recording problems.
    private async Task TellFailedAsync(Entry e, string why, CancellationToken ct)
    {
        Note($"{e.Name}: interrupted by the restart and not recording; alert raised");
        await _center.RaiseAsync(
            Report(e, $"The recording stopped when Jellyfin went down and hasn't started again. {why} The programme runs until {e.EndUtc.ToLocalTime():h:mm tt}."),
            ct).ConfigureAwait(false);
    }

    // From the last write to the old file to the start of the new one.
    private TimeSpan? Lost(Entry e)
    {
        try
        {
            var now = _recordings.GetActiveRecordingPath(e.TimerId);
            if (string.IsNullOrEmpty(e.Path) || !File.Exists(e.Path) || string.IsNullOrEmpty(now) || now == e.Path)
            {
                return null;
            }

            var stopped = File.GetLastWriteTimeUtc(e.Path);
            var started = File.GetCreationTimeUtc(now);
            if (started.Year < 2000 || started < stopped)
            {
                started = DateTime.UtcNow;
            }

            var gap = started - stopped;
            return gap > TimeSpan.Zero && gap < TimeSpan.FromHours(12) ? gap : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Describe(TimeSpan t) => t.TotalSeconds < 90 ? $"{t.TotalSeconds:0} s" : $"{t.TotalMinutes:0} min";

    private async Task WaitForCleanupAsync(CancellationToken ct)
    {
        var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var deadline = DateTime.UtcNow + CleanWaitMax;
        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        while (DateTime.UtcNow < deadline)
        {
            var task = _serviceProvider.GetService<ITaskManager>()?.ScheduledTasks
                .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, "DeleteTranscodeFiles", StringComparison.Ordinal));
            if (task is null
                || !task.Triggers.Any(tr => tr.Type.ToString()!.Contains("Startup", StringComparison.Ordinal))
                || (task.State == TaskState.Idle && task.LastExecutionResult?.EndTimeUtc > started))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    // Under _lock. Written to a temp file and moved, so a power cut mid-write leaves the old log.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            var tmp = LogFile + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, _log.Values.OrderBy(x => x.StartUtc).ToList());
                fs.Flush(true);
            }

            File.Move(tmp, LogFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the recording log");
        }
    }

    private Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(LogFile) && JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(LogFile)) is { } list)
            {
                return list.Where(x => !string.IsNullOrEmpty(x.TimerId))
                    .GroupBy(x => x.TimerId, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the recording log");
        }

        return FromFirstBuild();
    }

    // The first build of this (2026-11-18) only noted running recordings, in running-recordings.json:
    // taken over once, so installing this one mid-recording doesn't lose that recording.
    private Dictionary<string, Entry> FromFirstBuild()
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var old = Path.Combine(_paths.DataPath, "jellydvr-schedule", "running-recordings.json");
        try
        {
            if (File.Exists(old))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(old));
                var saved = doc.RootElement.TryGetProperty("SavedUtc", out var su) && su.TryGetDateTime(out var sd) ? sd.ToUniversalTime() : DateTime.UtcNow;
                if (doc.RootElement.TryGetProperty("Recordings", out var recs))
                {
                    foreach (var r in recs.EnumerateArray())
                    {
                        var id = r.GetProperty("TimerId").GetString() ?? string.Empty;
                        result[id] = new Entry
                        {
                            TimerId = id,
                            ProgramId = r.GetProperty("ProgramId").GetString() ?? string.Empty,
                            Name = r.GetProperty("Name").GetString() ?? string.Empty,
                            StartUtc = saved,
                            EndUtc = r.GetProperty("EndUtc").GetDateTime().ToUniversalTime(),
                            NotedUtc = saved
                        };
                    }
                }

                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
        {
            _logger.LogDebug(ex, "Could not take over the first build's notes");
        }

        return result;
    }

    // timers.json status by the DVR's timer id (GetTimers leaves Completed ones out).
    private Dictionary<string, string> TimerStates()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var file = Path.Combine(_paths.DataPath, "livetv", "timers.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    if (t.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String && t.TryGetProperty("Status", out var s))
                    {
                        result[id.GetString()!] = s.ValueKind == JsonValueKind.String ? s.GetString()! : StatusName(s.GetInt32());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Can't read Jellyfin's timers.json");
        }

        return result;
    }

    private static string StatusName(int value) => Enum.IsDefined(typeof(RecordingStatus), value) ? ((RecordingStatus)value).ToString() : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Note(string message)
    {
        lock (_recent)
        {
            _recent.Insert(0, DateTime.Now.ToString("ddd h:mm tt", System.Globalization.CultureInfo.InvariantCulture) + " · " + message);
            if (_recent.Count > 50)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }
        }
    }
}
