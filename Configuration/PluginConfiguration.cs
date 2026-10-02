using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DvrSchedule.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const string ModePreview = "preview";
    public const string ModeActive = "active";

    /// <summary>"preview": only show conflicts and what would be done. "active": move / skip recordings.</summary>
    public string Mode { get; set; } = ModePreview;

    /// <summary>How many recordings can stream at once (0 = use Jellyfin's tuner count).</summary>
    public int TunerInputs { get; set; }

    /// <summary>How far ahead to look for conflicts (days).</summary>
    public int LookAheadDays { get; set; } = 7;

    /// <summary>How often to check (minutes); also shortly after recordings are scheduled.</summary>
    public int CheckIntervalMinutes { get; set; } = 30;

    /// <summary>One-off recordings (a single programme chosen by hand) outrank every series recording.</summary>
    public bool OneOffAboveSeries { get; set; } = true;

    /// <summary>Resolve a conflict by recording another airing of the same episode when one fits.</summary>
    public bool MoveToOtherAiring { get; set; } = true;

    public const string KeepEarliest = "earliest";
    public const string KeepFirstRun = "first-run";

    /// <summary>When the same episode is scheduled more than once, record only one copy.</summary>
    public bool RecordEachEpisodeOnce { get; set; } = true;

    /// <summary>Which copy to keep: "earliest" airing, or "first-run" (a non-repeat airing if there is one).</summary>
    public string KeepCopy { get; set; } = KeepEarliest;

    /// <summary>Put a skipped recording back on the schedule once it fits again (another recording was cancelled or moved).</summary>
    public bool RestoreSkipped { get; set; } = true;

    public const string ViewersWarn = "warn";
    public const string ViewersRecord = "record";
    public const string ViewersFirst = "viewers";

    /// <summary>
    /// A recording is about to start and every tuner input is taken, some by people watching live TV:
    /// "warn": tell the viewer whose input is needed, then stop their live TV when the recording starts;
    /// "record": stop it without a warning; "viewers": never interrupt, move or skip the recording instead
    /// (and start it late if the viewer stops while it's still on).
    /// </summary>
    public string LiveViewerPolicy { get; set; } = ViewersWarn;

    /// <summary>How long before the recording starts the viewer is warned (minutes).</summary>
    public int WarnMinutes { get; set; } = 2;

    /// <summary>A skipped recording whose programme is already on starts late (records the rest) once an input is free.</summary>
    public bool StartLateWhenFree { get; set; } = true;

    /// <summary>Series recordings (series timer ids), highest priority first.</summary>
    public string[] SeriesPriority { get; set; } = Array.Empty<string>();
}
