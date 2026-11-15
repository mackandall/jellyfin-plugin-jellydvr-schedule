using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Alerts;

/// <summary>
/// Runs the alerts: picks up other plugins' reports from the inbox, checks the recordings drives'
/// free space and the guide's age, and sends reminders for problems still open.
/// </summary>
public sealed class AlertService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);

    private readonly AlertCenter _center;
    private readonly IConfigurationManager _config;
    private readonly ITaskManager _tasks;
    private readonly ILogger<AlertService> _logger;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private DateTime _lastCheck = DateTime.MinValue;
    private DateTime? _guideOkUtc;

    public AlertService(AlertCenter center, IConfigurationManager config, ITaskManager tasks, ILogger<AlertService> logger)
    {
        _center = center;
        _config = config;
        _tasks = tasks;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _center.ReadInboxAsync(stoppingToken).ConfigureAwait(false);
                    if (DateTime.UtcNow - _lastCheck >= CheckEvery)
                    {
                        _lastCheck = DateTime.UtcNow;
                        await CheckSpaceAsync(stoppingToken).ConfigureAwait(false);
                        await CheckGuideAsync(stoppingToken).ConfigureAwait(false);
                        await _center.RemindAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Alert checks failed");
                }

                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The folders recordings go to (Jellyfin's Live TV settings).</summary>
    private IEnumerable<string> RecordingFolders()
    {
        var o = _config.GetConfiguration<LiveTvOptions>("livetv");
        var paths = new[] { o.RecordingPath, o.MovieRecordingPath, o.SeriesRecordingPath }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();
        if (paths.Count == 0)
        {
            paths.Add(Path.Combine(_config.CommonApplicationPaths.DataPath, "livetv", "recordings"));
        }

        return paths.Distinct(StringComparer.Ordinal);
    }

    private async Task CheckSpaceAsync(CancellationToken ct)
    {
        var cfg = Config;
        var warnBytes = Math.Max(1, cfg.DiskFreeWarnGb) * 1_000_000_000L;

        // One check per drive, named by the first recordings folder on it.
        var drives = new Dictionary<string, (string Folder, long Free, long Size)>(StringComparer.Ordinal);
        foreach (var folder in RecordingFolders())
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                var d = new DriveInfo(folder);
                drives.TryAdd(d.RootDirectory.FullName, (folder, d.AvailableFreeSpace, d.TotalSize));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogDebug(ex, "Could not read free space for {Folder}", folder);
            }
        }

        foreach (var (root, (folder, free, size)) in drives)
        {
            var key = "space:" + root;
            if (free < warnBytes)
            {
                await _center.RaiseAsync(
                    new AlertReport
                    {
                        Kind = AlertKinds.Space,
                        Key = key,
                        Title = "Recordings drive nearly full",
                        Text = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} has {1:0.#} GB free of {2:0} GB (warning below {3} GB). New recordings may fail when it fills up.",
                            folder,
                            free / 1e9,
                            size / 1e9,
                            cfg.DiskFreeWarnGb),
                        Where = folder
                    },
                    ct).ConfigureAwait(false);
            }
            else if (_center.IsOpen(key))
            {
                await _center.ResolveAsync(key, string.Format(CultureInfo.InvariantCulture, "{0} has {1:0} GB free again.", folder, free / 1e9), true, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task CheckGuideAsync(CancellationToken ct)
    {
        var cfg = Config;
        var task = _tasks.ScheduledTasks.FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, "RefreshGuide", StringComparison.Ordinal));
        if (task is null || task.State == TaskState.Running)
        {
            return;
        }

        if (task.LastExecutionResult is { Status: TaskCompletionStatus.Completed } last)
        {
            _guideOkUtc = last.EndTimeUtc;
        }

        // Unknown (never ran since a reset, or the last run failed and none worked since this start):
        // count from when the server started.
        var since = _guideOkUtc ?? _startedUtc;
        const string Key = "guide";
        var stale = TimeSpan.FromHours(Math.Max(1, cfg.GuideStaleHours));
        if (DateTime.UtcNow - since > stale)
        {
            var why = task.LastExecutionResult is { Status: not TaskCompletionStatus.Completed } failed
                ? " The last refresh, " + AlertCenter.When(failed.EndTimeUtc) + ", ended " + failed.Status.ToString().ToLowerInvariant()
                    + (string.IsNullOrEmpty(failed.ErrorMessage) ? "." : ": " + failed.ErrorMessage)
                : string.Empty;
            await _center.RaiseAsync(
                new AlertReport
                {
                    Kind = AlertKinds.Guide,
                    Key = Key,
                    Title = "Guide not updated",
                    Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "No new guide data for {0:0} hours (last good refresh {1}).{2} Recordings scheduled from old guide data can miss programmes.",
                        (DateTime.UtcNow - since).TotalHours,
                        _guideOkUtc is { } ok ? AlertCenter.When(ok) : "not since the server started",
                        why)
                },
                ct).ConfigureAwait(false);
        }
        else if (_center.IsOpen(Key))
        {
            await _center.ResolveAsync(Key, "The guide was updated " + AlertCenter.When(since) + ".", true, ct).ConfigureAwait(false);
        }
    }
}
