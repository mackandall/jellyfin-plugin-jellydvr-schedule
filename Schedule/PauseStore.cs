using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// Paused series recordings. Jellyfin's series recordings can't be switched off, only deleted (and
/// one can't be set up again while the show has no airing in the guide), so a pause leaves the series
/// recording as it is and cancels the episodes it schedules instead, remembering which, so resuming
/// puts exactly those back (not ones cancelled by hand).
///
/// Kept in <c>&lt;data&gt;/jellydvr-schedule/pauses.json</c>.
/// </summary>
public sealed class PauseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _file;
    private readonly ILogger<PauseStore> _logger;
    private List<Pause> _pauses;

    public PauseStore(IApplicationPaths paths, ILogger<PauseStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(paths.DataPath, "jellydvr-schedule");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "pauses.json");
        _pauses = Load();
    }

    /// <summary>A paused series recording.</summary>
    public sealed class Pause
    {
        public string SeriesTimerId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public DateTime Since { get; set; }

        /// <summary>Resumes by itself at this time (UTC); null: until resumed by hand. Airings from then on record as usual.</summary>
        public DateTime? Until { get; set; }

        /// <summary>Paused until the guide shows the next season's premiere: then <see cref="Until"/> is set to it.</summary>
        public bool UntilPremiere { get; set; }

        public List<Held> Cancelled { get; set; } = new();

        /// <summary>Whether an airing starting at <paramref name="startUtc"/> is held back by this pause.</summary>
        public bool Covers(DateTime startUtc) => Until is not { } until || startUtc < until;
    }

    /// <summary>An episode the pause cancelled, to put back on resume.</summary>
    public sealed class Held
    {
        public Guid ProgramId { get; set; }

        public string Title { get; set; } = string.Empty;

        public DateTime Start { get; set; }

        public int PrePaddingSeconds { get; set; }

        public int PostPaddingSeconds { get; set; }
    }

    public IReadOnlyList<Pause> Pauses
    {
        get
        {
            lock (_lock)
            {
                return _pauses.ToList();
            }
        }
    }

    public Pause? Get(string seriesTimerId)
    {
        lock (_lock)
        {
            return Find(seriesTimerId);
        }
    }

    /// <summary>Pauses a series recording, or changes how long an existing pause lasts.</summary>
    public Pause Set(string seriesTimerId, string name, DateTime? until, bool untilPremiere = false)
    {
        lock (_lock)
        {
            var pause = SetLocked(seriesTimerId, name, until, untilPremiere);
            Save();
            return pause;
        }
    }

    /// <summary>Pauses several series recordings until the same time, saving once; returns how many.</summary>
    public int SetMany(IEnumerable<(string SeriesTimerId, string Name)> series, DateTime? until)
    {
        lock (_lock)
        {
            var count = 0;
            foreach (var (id, name) in series)
            {
                SetLocked(id, name, until, false);
                count++;
            }

            if (count > 0)
            {
                Save();
            }

            return count;
        }
    }

    private Pause SetLocked(string seriesTimerId, string name, DateTime? until, bool untilPremiere)
    {
        var pause = Find(seriesTimerId);
        if (pause is null)
        {
            pause = new Pause { SeriesTimerId = seriesTimerId, Since = DateTime.UtcNow };
            _pauses.Add(pause);
        }

        pause.Name = name;
        pause.Until = until;
        pause.UntilPremiere = untilPremiere && until is null;
        return pause;
    }

    // Caller holds the lock.
    private Pause? Find(string seriesTimerId) => _pauses.FirstOrDefault(p => Same(p.SeriesTimerId, seriesTimerId));

    public void AddHeld(string seriesTimerId, Held held)
    {
        lock (_lock)
        {
            var pause = Find(seriesTimerId);
            if (pause is null)
            {
                return;
            }

            pause.Cancelled.RemoveAll(h => h.ProgramId == held.ProgramId);
            pause.Cancelled.Add(held);
            Save();
        }
    }

    /// <summary>The premiere a pause waits for has turned up: it now ends at <paramref name="until"/>.</summary>
    public void PremiereFound(string seriesTimerId, DateTime until)
    {
        lock (_lock)
        {
            if (Find(seriesTimerId) is { } pause)
            {
                pause.Until = until;
                pause.UntilPremiere = false;
                Save();
            }
        }
    }

    /// <summary>Saves a pause changed in place (held episodes put back).</summary>
    public void Changed()
    {
        lock (_lock)
        {
            Save();
        }
    }

    /// <summary>Drops a pause; returns it (with the episodes it held) or null.</summary>
    public Pause? Remove(string seriesTimerId)
    {
        lock (_lock)
        {
            var pause = Find(seriesTimerId);
            if (pause is not null)
            {
                _pauses.Remove(pause);
                Save();
            }

            return pause;
        }
    }

    /// <summary>Forgets held episodes that have already aired.</summary>
    public void Prune(DateTime now)
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var p in _pauses)
            {
                changed |= p.Cancelled.RemoveAll(h => h.Start < now.AddDays(-1)) > 0;
            }

            if (changed)
            {
                Save();
            }
        }
    }

    // Jellyfin hands out series timer ids both with and without dashes.
    public static bool Same(string? a, string? b) =>
        string.Equals(Plain(a), Plain(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A series timer id without dashes (the form the clients use).</summary>
    public static string Plain(string? id) => (id ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal);

    private List<Pause> Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                return JsonSerializer.Deserialize<List<Pause>>(File.ReadAllText(_file)) ?? new List<Pause>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {File}", _file);
        }

        return new List<Pause>();
    }

    private void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_pauses, JsonOptions));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {File}", _file);
        }
    }
}
