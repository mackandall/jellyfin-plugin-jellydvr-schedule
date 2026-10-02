using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// What the planner did to airings it won't record (skipped, moved, dropped as a duplicate), and the
/// airings someone chose with "Record this instead". A cancelled recording drops out of Jellyfin's
/// timer list, so without this the plan forgets it ever existed: this is what the guide marks read,
/// what "restore when there's room" works from, and what survives a restart.
///
/// Kept in <c>&lt;data&gt;/jellydvr-schedule/</c> as plain JSON so other plugins (Episode History's guide
/// marks) can read <c>outcomes.json</c> without a code dependency.
/// </summary>
public sealed class OutcomeStore
{
    public const string KindSkipped = "skipped";
    public const string KindMoved = "moved";
    public const string KindDuplicate = "duplicate";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _outcomesFile;
    private readonly string _pinsFile;
    private readonly ILogger<OutcomeStore> _logger;
    private List<Outcome> _outcomes;
    private List<Pin> _pins;

    public OutcomeStore(IApplicationPaths paths, ILogger<OutcomeStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(paths.DataPath, "jellydvr-schedule");
        Directory.CreateDirectory(dir);
        _outcomesFile = Path.Combine(dir, "outcomes.json");
        _pinsFile = Path.Combine(dir, "pins.json");
        _outcomes = Load<Outcome>(_outcomesFile);
        _pins = Load<Pin>(_pinsFile);
    }

    /// <summary>An airing the planner took off the schedule, and what it did instead.</summary>
    public sealed class Outcome
    {
        public Guid ProgramId { get; set; }

        public string? ShowId { get; set; }

        public string Title { get; set; } = string.Empty;

        public string ChannelName { get; set; } = string.Empty;

        /// <summary>Programme start and end (UTC, without padding).</summary>
        public DateTime Start { get; set; }

        public DateTime End { get; set; }

        /// <summary><see cref="KindSkipped"/>, <see cref="KindMoved"/> or <see cref="KindDuplicate"/>.</summary>
        public string Kind { get; set; } = KindSkipped;

        /// <summary>Human-readable: why, and where it is recorded instead.</summary>
        public string Detail { get; set; } = string.Empty;

        /// <summary>The airing recorded instead (moved / duplicate), if any.</summary>
        public Guid? RecordedInstead { get; set; }

        public string? SeriesTimerId { get; set; }

        public int PrePaddingSeconds { get; set; }

        public int PostPaddingSeconds { get; set; }

        public DateTime Time { get; set; }
    }

    /// <summary>An airing someone chose to record whatever else has to give way.</summary>
    public sealed class Pin
    {
        public Guid ProgramId { get; set; }

        public string Title { get; set; } = string.Empty;

        public DateTime End { get; set; }

        public DateTime CreatedUtc { get; set; }
    }

    public IReadOnlyList<Outcome> Outcomes
    {
        get
        {
            lock (_lock)
            {
                return _outcomes.ToList();
            }
        }
    }

    public IReadOnlyList<Pin> Pins
    {
        get
        {
            lock (_lock)
            {
                return _pins.ToList();
            }
        }
    }

    public Outcome? GetOutcome(Guid programId)
    {
        lock (_lock)
        {
            return _outcomes.FirstOrDefault(o => o.ProgramId == programId);
        }
    }

    public bool IsPinned(Guid programId)
    {
        lock (_lock)
        {
            return _pins.Any(p => p.ProgramId == programId);
        }
    }

    public void SetOutcome(Outcome outcome)
    {
        lock (_lock)
        {
            _outcomes.RemoveAll(o => o.ProgramId == outcome.ProgramId);
            _outcomes.Add(outcome);
            Save(_outcomesFile, _outcomes);
        }
    }

    /// <summary>Forgets outcomes for these airings (they are on the schedule again).</summary>
    public int RemoveOutcomes(IEnumerable<Guid> programIds)
    {
        var ids = programIds.ToHashSet();
        lock (_lock)
        {
            var n = _outcomes.RemoveAll(o => ids.Contains(o.ProgramId));
            if (n > 0)
            {
                Save(_outcomesFile, _outcomes);
            }

            return n;
        }
    }

    public void AddPin(Pin pin)
    {
        lock (_lock)
        {
            _pins.RemoveAll(p => p.ProgramId == pin.ProgramId);
            _pins.Add(pin);
            Save(_pinsFile, _pins);
        }
    }

    public bool RemovePin(Guid programId)
    {
        lock (_lock)
        {
            if (_pins.RemoveAll(p => p.ProgramId == programId) == 0)
            {
                return false;
            }

            Save(_pinsFile, _pins);
            return true;
        }
    }

    /// <summary>Drops everything about airings that have ended.</summary>
    public void Prune(DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_outcomes.RemoveAll(o => o.End < nowUtc) > 0)
            {
                Save(_outcomesFile, _outcomes);
            }

            if (_pins.RemoveAll(p => p.End < nowUtc) > 0)
            {
                Save(_pinsFile, _pins);
            }
        }
    }

    private List<T> Load<T>(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(file)) ?? new List<T>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {File}; starting empty", file);
        }

        return new List<T>();
    }

    // Write-then-rename so a reader (another plugin) never sees a half-written file.
    private void Save<T>(string file, List<T> items)
    {
        try
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save {File}", file);
        }
    }
}
