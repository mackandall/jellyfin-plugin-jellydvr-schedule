using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Overruns;

/// <summary>
/// Joins a game's follow-up recordings onto its first recording once all of them are done, without
/// re-encoding (ffmpeg's concat, as Episode History joins the pieces of an interrupted recording).
/// The joined file is checked against the parts' lengths before it replaces the first recording;
/// each follow-up then leaves the library and is kept hidden beside it for a few days.
/// </summary>
public sealed class FollowUpJoiner
{
    /// <summary>Seconds skipped at the start of each follow-up: the overlap with the recording before it, less the tuner's start-up time.</summary>
    public const double SkipSeconds = 15;

    private const string HiddenMarker = ".followup";
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan KeepHidden = TimeSpan.FromDays(3);

    private readonly ILogger<FollowUpJoiner> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IRecordingsManager _recordings;
    private readonly OverrunStore _store;

    public FollowUpJoiner(
        ILogger<FollowUpJoiner> logger,
        IServiceProvider serviceProvider,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IRecordingsManager recordings,
        OverrunStore store)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _recordings = recordings;
        _store = store;
    }

    public async Task JoinReadyAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var chain in _store.Chains().Where(c => c.State == "pending" && c.TimerIds.Count >= 2))
        {
            if (chain.TimerIds.Any(id => _recordings.GetActiveRecordingPath(id) is not null))
            {
                continue; // still recording
            }

            var parts = chain.TimerIds.Where(chain.Paths.ContainsKey).Select(id => chain.Paths[id]).Where(File.Exists).ToList();
            var allSeen = chain.TimerIds.All(chain.Paths.ContainsKey);
            if (!allSeen && now - chain.CreatedUtc < TimeSpan.FromHours(8))
            {
                continue; // a follow-up hasn't recorded yet
            }

            if (parts.Any(p => now - File.GetLastWriteTimeUtc(p) < Settle))
            {
                continue;
            }

            if (parts.Count < 2 || chain.Tries >= 3)
            {
                chain.State = parts.Count < 2 ? "nothing" : "failed";
                chain.Note = parts.Count < 2 ? "Only one part was recorded; nothing to join" : chain.Note;
                chain.DoneUtc = now;
                _store.Save(chain);
                continue;
            }

            chain.Tries++;
            try
            {
                var result = await JoinAsync(parts, ct).ConfigureAwait(false);
                chain.Note = result;
                chain.State = result.StartsWith("Joined", StringComparison.Ordinal) ? "joined" : "pending";
                if (chain.State == "joined")
                {
                    chain.DoneUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                chain.Note = "Join failed: " + ex.Message;
                _logger.LogWarning(ex, "Joining the follow-ups of {Title} failed", chain.Title);
            }

            _store.Save(chain);
        }

        DeleteOldHidden(now);
    }

    private async Task<string> JoinAsync(List<string> parts, CancellationToken ct)
    {
        var encoder = _serviceProvider.GetService<IMediaEncoder>();
        if (encoder is null || string.IsNullOrEmpty(encoder.EncoderPath) || string.IsNullOrEmpty(encoder.ProbePath))
        {
            return "Couldn't find ffmpeg";
        }

        var target = parts[0];
        var dir = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        var list = Path.Combine(dir, "." + stem + ".joining.txt");
        var output = Path.Combine(dir, "." + stem + ".joining" + ext);
        try
        {
            double expected = 0;
            for (var i = 0; i < parts.Count; i++)
            {
                var seconds = await DurationAsync(encoder.ProbePath, parts[i], ct).ConfigureAwait(false);
                if (seconds is not > 0)
                {
                    return "Couldn't read the length of " + Path.GetFileName(parts[i]);
                }

                expected += i == 0 ? seconds.Value : Math.Max(0, seconds.Value - SkipSeconds);
            }

            var lines = new List<string>();
            for (var i = 0; i < parts.Count; i++)
            {
                lines.Add("file '" + parts[i].Replace("'", "'\\''", StringComparison.Ordinal) + "'");
                if (i > 0)
                {
                    lines.Add("inpoint " + SkipSeconds.ToString(CultureInfo.InvariantCulture));
                }
            }

            await File.WriteAllLinesAsync(list, lines, new UTF8Encoding(false), ct).ConfigureAwait(false);
            var (code, error) = await RunAsync(
                encoder.EncoderPath,
                new[] { "-hide_banner", "-loglevel", "error", "-y", "-fflags", "+genpts", "-f", "concat", "-safe", "0", "-i", list, "-map", "0", "-ignore_unknown", "-c", "copy", output },
                ct).ConfigureAwait(false);
            if (code != 0 || !File.Exists(output))
            {
                return "ffmpeg couldn't join the parts (" + Tail(error) + ")";
            }

            var joined = await DurationAsync(encoder.ProbePath, output, ct).ConfigureAwait(false);
            var j = joined ?? 0;
            if (j < expected - 30)
            {
                File.Delete(output);
                return $"The joined file came out short ({j:0} s of {expected:0} s); the parts were left as they are";
            }

            // The follow-ups leave the library first, then the joined file takes the first one's place.
            foreach (var part in parts.Skip(1))
            {
                if (_libraryManager.FindByPath(part, false) is { } item)
                {
                    _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                }

                var hidden = Path.Combine(Path.GetDirectoryName(part)!, "." + Path.GetFileNameWithoutExtension(part) + HiddenMarker + Path.GetExtension(part));
                File.Move(part, hidden, true);
                _libraryMonitor.ReportFileSystemChanged(Path.GetDirectoryName(part)!);
            }

            File.Move(output, target, true);
            _libraryMonitor.ReportFileSystemChanged(dir);
            _logger.LogInformation("Sports overruns: joined {Count} recordings into {File} ({Minutes:0} min)", parts.Count, Path.GetFileName(target), j / 60);
            return $"Joined {parts.Count} recordings ({j / 60:0} min)";
        }
        finally
        {
            TryDelete(list);
            if (File.Exists(output))
            {
                TryDelete(output);
            }
        }
    }

    // Follow-ups kept hidden after a join: gone after a few days.
    private void DeleteOldHidden(DateTime now)
    {
        foreach (var chain in _store.Chains().Where(c => c.State == "joined" && c.DoneUtc is { } d && now - d > KeepHidden))
        {
            foreach (var part in chain.TimerIds.Skip(1).Where(chain.Paths.ContainsKey).Select(id => chain.Paths[id]))
            {
                var hidden = Path.Combine(Path.GetDirectoryName(part)!, "." + Path.GetFileNameWithoutExtension(part) + HiddenMarker + Path.GetExtension(part));
                TryDelete(hidden);
            }

            chain.State = "done";
            _store.Save(chain);
        }
    }

    private static async Task<double?> DurationAsync(string ffprobe, string file, CancellationToken ct)
    {
        var (code, output) = await RunAsync(ffprobe, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file }, ct, stdout: true).ConfigureAwait(false);
        return code == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static async Task<(int Code, string Text)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, bool stdout = false)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start " + exe);
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, stdout ? await outTask.ConfigureAwait(false) : await errTask.ConfigureAwait(false));
    }

    private static string Tail(string s) => s.Length > 200 ? s[^200..].Trim() : s.Trim();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
