using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Alerts;

/// <summary>The kinds of problem, as the settings page and the clients know them.</summary>
public static class AlertKinds
{
    /// <summary>A recording that produced nothing (Episode History's ledger).</summary>
    public const string Failed = "failed";

    /// <summary>A recording that came out short or whose picture stopped (stall watch, Episode History).</summary>
    public const string Short = "short";

    /// <summary>A recording that hasn't started: a tuner is stuck or not answering.</summary>
    public const string Tuner = "tuner";

    /// <summary>A recordings drive nearly full.</summary>
    public const string Space = "space";

    /// <summary>No new guide data for a while.</summary>
    public const string Guide = "guide";

    public static readonly string[] All = { Failed, Short, Tuner, Space, Guide };
}

/// <summary>A problem reported to <see cref="AlertCenter"/> (also the shape of an inbox file).</summary>
public sealed class AlertReport
{
    /// <summary>One of <see cref="AlertKinds"/>.</summary>
    public string Kind { get; set; } = AlertKinds.Failed;

    /// <summary>Names the problem: a second report with the same key is the same problem.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>One line: "Animal Control didn't record".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>What happened.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>What happens next ("Re-record set for Tue 8:00 PM"), if anything.</summary>
    public string? Next { get; set; }

    /// <summary>"KOKO 5.1", "HDHomeRun tuner 2".</summary>
    public string? Where { get; set; }

    /// <summary>When the programme aired (UTC), for recordings.</summary>
    public DateTime? AiredUtc { get; set; }

    /// <summary>The recording (library item), when there is one.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>The guide entry, when there is one.</summary>
    public Guid? ProgramId { get; set; }

    /// <summary>The plugin that saw it.</summary>
    public string Source { get; set; } = "JellyDVR Schedule";

    /// <summary>In an inbox file: this report clears the problem with this key.</summary>
    public bool Fixed { get; set; }

    /// <summary>Only updates the open problem with this key (e.g. its <see cref="Next"/>); never opens one.</summary>
    public bool UpdateOnly { get; set; }
}

/// <summary>One problem, open or closed.</summary>
public sealed class Alert
{
    public const string Open = "open";
    public const string FixedState = "fixed";
    public const string Dismissed = "dismissed";

    /// <summary>Over without being fixed (the programme ended); another report says what came of it.</summary>
    public const string Ended = "ended";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Kind { get; set; } = AlertKinds.Failed;

    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string? Next { get; set; }

    public string? Where { get; set; }

    public DateTime? AiredUtc { get; set; }

    public Guid? ItemId { get; set; }

    public Guid? ProgramId { get; set; }

    public string Source { get; set; } = string.Empty;

    /// <summary><see cref="Open"/>, <see cref="FixedState"/>, <see cref="Dismissed"/> or <see cref="Ended"/>.</summary>
    public string State { get; set; } = Open;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime? ClosedUtc { get; set; }

    /// <summary>How it cleared ("working again at 06:12").</summary>
    public string? ClosedText { get; set; }

    /// <summary>Where it was sent: "phone", "email" (the TV reads the list itself).</summary>
    public List<string> SentTo { get; set; } = new();

    public DateTime? LastSentUtc { get; set; }

    /// <summary>Times it was reported again while open.</summary>
    public int Repeats { get; set; }
}

/// <summary>
/// Problems with recordings, tuners, space and the guide, from this plugin's watchdogs and checks
/// and from other plugins (Episode History) through an inbox folder, so no plugin needs another's
/// code: a plugin drops an <see cref="AlertReport"/> as JSON into
/// <c>&lt;data&gt;/jellydvr-alerts/inbox/</c> and it is picked up within seconds.
///
/// Each problem is told once (a repeat of an open one is counted, not sent), again as a reminder
/// while it stays open, and once more when it clears by itself. The TV client reads the list;
/// phone (ntfy) and email are sent from here, following each kind's choices on the Alerts page,
/// silently on the phone in quiet hours. Kept in <c>&lt;data&gt;/jellydvr-schedule/alerts.json</c>.
/// </summary>
public sealed class AlertCenter
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    private const int KeepMost = 300;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly object _lock = new();
    private readonly string _file;
    private readonly AlertSenders _senders;
    private readonly ILogger<AlertCenter> _logger;
    private List<Alert> _alerts;

    public AlertCenter(IApplicationPaths paths, AlertSenders senders, ILogger<AlertCenter> logger)
    {
        _senders = senders;
        _logger = logger;
        var dir = Path.Combine(paths.DataPath, "jellydvr-schedule");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "alerts.json");
        InboxPath = Path.Combine(paths.DataPath, "jellydvr-alerts", "inbox");
        Directory.CreateDirectory(InboxPath);
        _alerts = Load();
    }

    /// <summary>Where other plugins drop reports.</summary>
    public string InboxPath { get; }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Where a kind goes, from the settings (or its default).</summary>
    public static AlertRoute RouteFor(string kind, PluginConfiguration cfg) =>
        cfg.AlertRoutes?.FirstOrDefault(r => string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase)) ?? AlertRoute.Default(kind);

    /// <summary>All kept alerts, newest first.</summary>
    public List<Alert> List()
    {
        lock (_lock)
        {
            return _alerts.OrderByDescending(a => a.CreatedUtc).Select(Copy).ToList();
        }
    }

    /// <summary>A problem seen: a new alert, or a repeat of the open one with its key.</summary>
    public async Task RaiseAsync(AlertReport r, CancellationToken ct = default, bool send = true)
    {
        if (string.IsNullOrWhiteSpace(r.Key) || !AlertKinds.All.Contains(r.Kind))
        {
            _logger.LogWarning("Alert ignored (no key or unknown kind {Kind}): {Title}", r.Kind, r.Title);
            return;
        }

        Alert? fresh = null;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var open = _alerts.FirstOrDefault(a => a.Key == r.Key && a.State == Alert.Open);
            if (open is not null)
            {
                open.Repeats += r.UpdateOnly ? 0 : 1;
                open.UpdatedUtc = now;
                open.Text = r.Text.Length > 0 ? r.Text : open.Text;
                open.Next = r.Next ?? open.Next;
                open.ItemId ??= r.ItemId;
                open.ProgramId ??= r.ProgramId;
            }
            else if (!r.UpdateOnly)
            {
                fresh = new Alert
                {
                    Kind = r.Kind,
                    Key = r.Key,
                    Title = r.Title,
                    Text = r.Text,
                    Next = r.Next,
                    Where = r.Where,
                    AiredUtc = r.AiredUtc,
                    ItemId = r.ItemId,
                    ProgramId = r.ProgramId,
                    Source = r.Source,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                _alerts.Add(fresh);
            }

            SaveLocked();
        }

        if (fresh is null || !send)
        {
            return;
        }

        _logger.LogWarning("Alert ({Kind}): {Title}. {Text}", fresh.Kind, fresh.Title, fresh.Text);
        var sent = await _senders.SendAsync(fresh, AlertSenders.Message.New, RouteFor(fresh.Kind, Config), Config, ct).ConfigureAwait(false);
        MarkSent(fresh.Id, sent);
    }

    /// <summary>The problem with this key cleared by itself (or, <paramref name="tell"/> off, is just over).</summary>
    public async Task ResolveAsync(string key, string text, bool tell = true, CancellationToken ct = default)
    {
        Alert? closed = null;
        lock (_lock)
        {
            var open = _alerts.FirstOrDefault(a => a.Key == key && a.State == Alert.Open);
            if (open is null)
            {
                return;
            }

            open.State = tell ? Alert.FixedState : Alert.Ended;
            open.ClosedUtc = open.UpdatedUtc = DateTime.UtcNow;
            open.ClosedText = text;
            SaveLocked();
            closed = Copy(open);
        }

        _logger.LogInformation("Alert cleared ({Kind}): {Title}. {Text}", closed.Kind, closed.Title, text);
        var cfg = Config;
        if (tell && cfg.TellWhenFixed && closed.SentTo.Count > 0)
        {
            // The fixed message goes where the problem went.
            var route = new AlertRoute { Kind = closed.Kind, Phone = closed.SentTo.Contains("phone"), Email = closed.SentTo.Contains("email") };
            await _senders.SendAsync(closed, AlertSenders.Message.Fixed, route, cfg, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Someone saw it and doesn't want it listed as open.</summary>
    public bool Dismiss(string id)
    {
        lock (_lock)
        {
            var a = _alerts.FirstOrDefault(x => x.Id == id);
            if (a is null)
            {
                return false;
            }

            if (a.State == Alert.Open)
            {
                a.State = Alert.Dismissed;
                a.ClosedUtc = a.UpdatedUtc = DateTime.UtcNow;
                SaveLocked();
            }

            return true;
        }
    }

    /// <summary>Dismisses every open problem (after a known outage, say); returns how many.</summary>
    public int DismissAll()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var open = _alerts.Where(a => a.State == Alert.Open).ToList();
            foreach (var a in open)
            {
                a.State = Alert.Dismissed;
                a.ClosedUtc = a.UpdatedUtc = now;
            }

            if (open.Count > 0)
            {
                SaveLocked();
            }

            return open.Count;
        }
    }

    /// <summary>Whether a problem with this key is open.</summary>
    public bool IsOpen(string key)
    {
        lock (_lock)
        {
            return _alerts.Any(a => a.Key == key && a.State == Alert.Open);
        }
    }

    /// <summary>Open problems not told for <see cref="PluginConfiguration.RemindHours"/>: told again.</summary>
    public async Task RemindAsync(CancellationToken ct)
    {
        var cfg = Config;
        if (cfg.RemindHours <= 0)
        {
            return;
        }

        var due = DateTime.UtcNow.AddHours(-cfg.RemindHours);
        List<Alert> remind;
        lock (_lock)
        {
            remind = _alerts.Where(a => a.State == Alert.Open && a.SentTo.Count > 0 && (a.LastSentUtc ?? a.CreatedUtc) < due).Select(Copy).ToList();
        }

        foreach (var a in remind)
        {
            var route = new AlertRoute { Kind = a.Kind, Phone = a.SentTo.Contains("phone"), Email = a.SentTo.Contains("email") };
            var sent = await _senders.SendAsync(a, AlertSenders.Message.Reminder, route, cfg, ct).ConfigureAwait(false);
            MarkSent(a.Id, sent.Count > 0 ? sent : a.SentTo);
        }
    }

    /// <summary>Reads and removes the reports other plugins dropped in the inbox.</summary>
    public async Task ReadInboxAsync(CancellationToken ct)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(InboxPath, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read the alerts inbox");
            return;
        }

        foreach (var f in files)
        {
            AlertReport? r = null;
            try
            {
                // A file still being written is left for the next look.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromSeconds(1))
                {
                    continue;
                }

                r = JsonSerializer.Deserialize<AlertReport>(await File.ReadAllTextAsync(f, ct).ConfigureAwait(false), JsonOptions);
                File.Delete(f);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Unreadable alert report {File}; removed", f);
                try
                {
                    File.Delete(f);
                }
                catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex2, "Could not remove {File}", f);
                }
            }

            if (r is null)
            {
                continue;
            }

            if (r.Fixed)
            {
                await ResolveAsync(r.Key, r.Text, true, ct).ConfigureAwait(false);
            }
            else
            {
                await RaiseAsync(r, ct).ConfigureAwait(false);
            }
        }
    }

    private void MarkSent(string id, List<string> sentTo)
    {
        if (sentTo.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            var a = _alerts.FirstOrDefault(x => x.Id == id);
            if (a is null)
            {
                return;
            }

            a.SentTo = a.SentTo.Union(sentTo).ToList();
            a.LastSentUtc = DateTime.UtcNow;
            SaveLocked();
        }
    }

    private List<Alert> Load()
    {
        try
        {
            return File.Exists(_file)
                ? JsonSerializer.Deserialize<List<Alert>>(File.ReadAllText(_file), JsonOptions) ?? new List<Alert>()
                : new List<Alert>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read {File}; starting with no alerts", _file);
            return new List<Alert>();
        }
    }

    private void SaveLocked()
    {
        var cutoff = DateTime.UtcNow - Keep;
        _alerts = _alerts
            .Where(a => a.State == Alert.Open || (a.ClosedUtc ?? a.UpdatedUtc) > cutoff)
            .OrderByDescending(a => a.CreatedUtc)
            .Take(KeepMost)
            .ToList();
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_alerts, JsonOptions));
            File.Move(tmp, _file, true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save {File}", _file);
        }
    }

    private static Alert Copy(Alert a)
    {
        var c = a.MemberwiseCloneAlert();
        c.SentTo = a.SentTo.ToList();
        return c;
    }

    /// <summary>"Sat 7:00 PM" in the server's local time.</summary>
    public static string When(DateTime utc) => utc.ToLocalTime().ToString("ddd h:mm tt", CultureInfo.InvariantCulture);
}

internal static class AlertCloneExtensions
{
    public static Alert MemberwiseCloneAlert(this Alert a) => new()
    {
        Id = a.Id,
        Kind = a.Kind,
        Key = a.Key,
        Title = a.Title,
        Text = a.Text,
        Next = a.Next,
        Where = a.Where,
        AiredUtc = a.AiredUtc,
        ItemId = a.ItemId,
        ProgramId = a.ProgramId,
        Source = a.Source,
        State = a.State,
        CreatedUtc = a.CreatedUtc,
        UpdatedUtc = a.UpdatedUtc,
        ClosedUtc = a.ClosedUtc,
        ClosedText = a.ClosedText,
        SentTo = a.SentTo,
        LastSentUtc = a.LastSentUtc,
        Repeats = a.Repeats
    };
}
