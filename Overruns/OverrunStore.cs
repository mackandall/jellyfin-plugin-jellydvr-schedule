using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Overruns;

/// <summary>
/// What the overrun watch changed, kept across restarts (&lt;data&gt;/jellydvr-schedule/overruns.json):
/// the padding a recording had before extra time was added (so a restart never adds it twice), and
/// the follow-up recordings of games still to be joined.
/// </summary>
public sealed class OverrunStore
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(4);

    private readonly string _file;
    private readonly ILogger<OverrunStore> _logger;
    private readonly object _lock = new();
    private Data _data = new();

    public OverrunStore(IApplicationPaths paths, ILogger<OverrunStore> logger)
    {
        _logger = logger;
        _file = Path.Combine(paths.DataPath, "jellydvr-schedule", "overruns.json");
        Load();
    }

    /// <summary>A recording given extra time.</summary>
    public sealed class Change
    {
        public string TimerId { get; set; } = string.Empty;

        public DateTime StartUtc { get; set; }

        public string Title { get; set; } = string.Empty;

        public int OriginalPrePadding { get; set; }

        public int OriginalPostPadding { get; set; }

        /// <summary>Extra minutes added on top of the original padding.</summary>
        public int ExtraMinutes { get; set; }

        /// <summary>Minutes the start was moved later (0 = not moved).</summary>
        public int LaterStartMinutes { get; set; }

        /// <summary>"game ran late", "overtime"…: shown with the recording.</summary>
        public string Reason { get; set; } = string.Empty;

        public DateTime ChangedUtc { get; set; }

        /// <summary>Someone chose "Keep the normal end": the watch leaves this recording alone.</summary>
        public bool UserKept { get; set; }

        /// <summary>Minutes added by hand ("Add 15 more minutes"), on top of the watch's own.</summary>
        public int UserExtraMinutes { get; set; }
    }

    /// <summary>The recordings of one game: the first, then follow-ups, joined into the first once all are done.</summary>
    public sealed class GameChain
    {
        public string FirstTimerId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public Guid ChannelId { get; set; }

        public string GameId { get; set; } = string.Empty;

        public string League { get; set; } = string.Empty;

        /// <summary>Timer ids in playing order (first, then follow-ups).</summary>
        public List<string> TimerIds { get; set; } = new();

        /// <summary>Recording files by timer id, noted while each records.</summary>
        public Dictionary<string, string> Paths { get; set; } = new();

        /// <summary>"pending", "joined", "failed", "nothing" (no follow-up was needed).</summary>
        public string State { get; set; } = "pending";

        public string Note { get; set; } = string.Empty;

        public int Tries { get; set; }

        /// <summary>When the first recording was set to end; follow-ups count their limit from here.</summary>
        public DateTime FirstEndUtc { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime? DoneUtc { get; set; }
    }

    private sealed class Data
    {
        public List<Change> Changes { get; set; } = new();

        public List<GameChain> Chains { get; set; } = new();
    }

    public Change? ChangeFor(string timerId, DateTime startUtc)
    {
        lock (_lock)
        {
            return _data.Changes.FirstOrDefault(c => c.TimerId == timerId && Math.Abs((c.StartUtc - startUtc).TotalMinutes) < 1);
        }
    }

    public IReadOnlyList<Change> Changes()
    {
        lock (_lock)
        {
            return _data.Changes.ToList();
        }
    }

    public void Save(Change change)
    {
        lock (_lock)
        {
            _data.Changes.RemoveAll(c => c.TimerId == change.TimerId && Math.Abs((c.StartUtc - change.StartUtc).TotalMinutes) < 1);
            _data.Changes.Add(change);
            SaveLocked();
        }
    }

    public List<GameChain> Chains()
    {
        lock (_lock)
        {
            return _data.Chains.ToList();
        }
    }

    public GameChain? ChainWith(string timerId)
    {
        lock (_lock)
        {
            return _data.Chains.FirstOrDefault(c => c.TimerIds.Contains(timerId));
        }
    }

    public void Save(GameChain chain)
    {
        lock (_lock)
        {
            if (!_data.Chains.Contains(chain))
            {
                _data.Chains.RemoveAll(c => c.FirstTimerId == chain.FirstTimerId);
                _data.Chains.Add(chain);
            }

            SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_file)) ?? new Data();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't read {File}", _file);
        }
    }

    private void SaveLocked()
    {
        var cutoff = DateTime.UtcNow - KeepFor;
        _data.Changes.RemoveAll(c => c.StartUtc < cutoff);
        _data.Chains.RemoveAll(c => c.CreatedUtc < cutoff && c.State != "pending");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't save {File}", _file);
        }
    }
}
