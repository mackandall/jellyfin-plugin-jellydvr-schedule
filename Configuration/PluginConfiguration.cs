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

    /// <summary>
    /// Alert the admins (on screen and in the activity log) when a recording hasn't started some time
    /// after it should have. Jellyfin opens live streams one at a time, so one tuner that never answers
    /// silently blocks every recording.
    /// </summary>
    public bool StuckRecordingAlert { get; set; } = true;

    /// <summary>How long after its start (with padding) a recording counts as not started (seconds).</summary>
    public int StuckRecordingSeconds { get; set; } = 90;

    /// <summary>Show the alert on the screens of admins using Jellyfin, besides the activity log.</summary>
    public bool StuckRecordingOnScreen { get; set; } = true;

    /// <summary>
    /// A new series recording whose episode is already on (Jellyfin starts recording it at once,
    /// part-way through): record that episode at a later airing instead, when there is one.
    /// </summary>
    public bool MoveRunningEpisode { get; set; } = true;

    /// <summary>Title/keyword recording rules, independent of guide ids.</summary>
    public FavouriteRule[] Favourites { get; set; } = Array.Empty<FavouriteRule>();

    /// <summary>
    /// Series recordings (series timer ids) and favourites ("fav:" + rule id), highest priority first.
    /// </summary>
    public string[] SeriesPriority { get; set; } = Array.Empty<string>();
}

/// <summary>
/// A favourite: records airings whose title matches, whatever id the guide gives the show, so a
/// guide switch or renumbering can't silently break it (a Jellyfin series recording is tied to the
/// guide's series id).
/// </summary>
public class FavouriteRule
{
    public const string MatchTitle = "title";
    public const string MatchKeyword = "keyword";
    public const string TypeAny = "any";
    public const string TypeSeries = "series";
    public const string TypeMovies = "movies";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The show's title (exact, ignoring case, punctuation, "The" and a trailing year) or a keyword.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>"title": the whole title matches; "keyword": the words appear in the title.</summary>
    public string Match { get; set; } = MatchTitle;

    /// <summary>Channel numbers or names, comma separated; empty = any channel.</summary>
    public string Channels { get; set; } = string.Empty;

    /// <summary>Only airings not marked as repeats.</summary>
    public bool NewOnly { get; set; }

    /// <summary>"any", "series" (episodes) or "movies".</summary>
    public string Type { get; set; } = TypeAny;

    /// <summary>Skip episodes Episode History marks as already in the library.</summary>
    public bool SkipInLibrary { get; set; } = true;

    public bool Enabled { get; set; } = true;
}
