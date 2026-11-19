using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DvrSchedule.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const string ModePreview = "preview";
    public const string ModeActive = "active";

    /// <summary>"preview": only show conflicts and what would be done. "active": move / skip recordings.</summary>
    public string Mode { get; set; } = ModePreview;

    public const string CountPerDevice = "device";
    public const string CountTotal = "total";

    /// <summary>
    /// "device": each tuner device (Live TV › tuner devices) has its own inputs and its channels
    /// only compete with each other. "total": every channel shares <see cref="TunerInputs"/>.
    /// </summary>
    public string TunerCounting { get; set; } = CountPerDevice;

    /// <summary>"total" counting: how many recordings can stream at once (0 = Jellyfin's tuner count).</summary>
    public int TunerInputs { get; set; }

    /// <summary>"device" counting: inputs per device where it differs from Jellyfin's tuner count.</summary>
    public TunerDeviceSetting[] TunerDevices { get; set; } = Array.Empty<TunerDeviceSetting>();

    /// <summary>
    /// "device" counting: also write each device's planned inputs into Jellyfin's own tuner count
    /// (the limit Jellyfin enforces), which Jellyfin never re-reads from the device and its dashboard
    /// can't edit for HDHomeRun-type tuners.
    /// </summary>
    public bool SyncJellyfinTunerCounts { get; set; }

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
    /// Watch recordings in progress for a stall: the file stops growing because the tuner went quiet
    /// (seen 2026-10-13: the capture stopped mid-film and Jellyfin went on "recording" nothing to
    /// the end). The admins are told, as for a recording that didn't start.
    /// </summary>
    public bool StallWatch { get; set; } = true;

    /// <summary>How long a recording's file may go without growing before it counts as stalled (seconds).</summary>
    public int StallSeconds { get; set; } = 90;

    /// <summary>
    /// Tune a stalled recording again: it is stopped and the rest of the programme is recorded as
    /// a new file, so a stall costs a minute or two and not the rest of the programme.
    /// </summary>
    public bool StallRetune { get; set; } = true;

    /// <summary>
    /// Pick up recordings a restart, crash or power cut interrupted and Jellyfin didn't resume (a clean
    /// shutdown marks them finished). Recordings are logged as they start; after any start of Jellyfin
    /// the rest of a programme still on is recorded as a second file, which Episode History joins.
    /// </summary>
    public bool ResumeAfterRestart { get; set; } = true;

    /// <summary>Most times one programme's recording is tuned again after stalling.</summary>
    public int StallMaxRetunes { get; set; } = 3;

    /// <summary>
    /// A new series recording whose episode is already on (Jellyfin starts recording it at once,
    /// part-way through): record that episode at a later airing instead, when there is one.
    /// </summary>
    public bool MoveRunningEpisode { get; set; } = true;

    // ---- Alerts (Alerts/AlertCenter.cs; the "JellyDVR Alerts" page) --------------------------

    /// <summary>Where each kind of problem is sent; kinds missing here use <see cref="AlertRoute.Default"/>.</summary>
    public AlertRoute[] AlertRoutes { get; set; } = Array.Empty<AlertRoute>();

    /// <summary>ntfy server (https://ntfy.sh or your own).</summary>
    public string NtfyServer { get; set; } = "https://ntfy.sh";

    /// <summary>ntfy topic; empty = phone alerts off.</summary>
    public string NtfyTopic { get; set; } = string.Empty;

    /// <summary>Access token for a ntfy server that needs one (optional).</summary>
    public string NtfyToken { get; set; } = string.Empty;

    /// <summary>Email address alerts go to; empty = email off.</summary>
    public string EmailTo { get; set; } = string.Empty;

    public string EmailFrom { get; set; } = string.Empty;

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public bool SmtpSsl { get; set; } = true;

    public string SmtpUser { get; set; } = string.Empty;

    public string SmtpPassword { get; set; } = string.Empty;

    /// <summary>JDVR: a popup listing new problems when the app opens.</summary>
    public bool TvPopupOnOpen { get; set; } = true;

    /// <summary>JDVR: a Problems tile on Home while any problem is open.</summary>
    public bool TvProblemsTile { get; set; } = true;

    /// <summary>JDVR: a popup during playback too (failed recordings only).</summary>
    public bool TvPopupDuringPlayback { get; set; }

    /// <summary>Phone alerts arrive silently between <see cref="QuietFrom"/> and <see cref="QuietTo"/> (local time).</summary>
    public bool QuietHours { get; set; } = true;

    public string QuietFrom { get; set; } = "23:00";

    public string QuietTo { get; set; } = "07:00";

    /// <summary>A failed recording still makes a sound in quiet hours.</summary>
    public bool FailedLoudInQuiet { get; set; } = true;

    /// <summary>Send a message when a problem clears by itself.</summary>
    public bool TellWhenFixed { get; set; } = true;

    /// <summary>Remind about a problem still open after this many hours (0 = never).</summary>
    public int RemindHours { get; set; } = 12;

    /// <summary>Warn when a recordings drive has less free space than this (GB).</summary>
    public int DiskFreeWarnGb { get; set; } = 50;

    /// <summary>Warn when the guide hasn't been refreshed for this many hours.</summary>
    public int GuideStaleHours { get; set; } = 24;

    // ---- Sports overruns (Overruns/; the "Sports overruns" page) ------------------------------

    public const string OverrunOff = "off";
    public const string ChannelShift = "shift";
    public const string ChannelNext = "next";
    public const string ChannelJoins = "joins";

    public const string GuideMarksAffected = "affected";

    public const string GuideMarksAll = "all";

    /// <summary>"off", "preview" (show what would change) or "active" (change recordings).</summary>
    public string OverrunMode { get; set; } = ModePreview;

    /// <summary>A game counts for a recording when it airs on the same channel up to this long before it (hours).</summary>
    public int OverrunLookBackHours { get; set; } = 3;

    /// <summary>Minutes added to every estimate from the game clock.</summary>
    public int OverrunMarginMinutes { get; set; } = 10;

    /// <summary>Most extra minutes one recording gets; over this it is capped and an alert goes out.</summary>
    public int OverrunCapMinutes { get; set; } = 90;

    /// <summary>How often the score is checked while a watched game is on (minutes).</summary>
    public int OverrunPollMinutes { get; set; } = 2;

    /// <summary>A game recording still going at its end gets follow-up recordings up to this many minutes in all (0 = none).</summary>
    public int OverrunFollowUpMinutes { get; set; } = 120;

    /// <summary>Also start the shows after a late game later (frees the tuner during the game).</summary>
    public bool OverrunMoveStart { get; set; }

    /// <summary>The recording's own end padding is kept on top of the extra time.</summary>
    public bool OverrunKeepPadding { get; set; } = true;

    /// <summary>Alert when a recording is capped or a follow-up can't be recorded.</summary>
    public bool OverrunAlerts { get; set; } = true;

    /// <summary>Fixed extra minutes per sport (<see cref="Overruns.SportKinds"/>), used when the game clock can't tell.</summary>
    public SportExtra[] OverrunSportMinutes { get; set; } = SportExtra.Defaults();

    /// <summary>What a late game does to the shows after it, per channel; channels not listed use <see cref="OverrunDefaultChannel"/>.</summary>
    public ChannelOverrun[] OverrunChannels { get; set; } = Array.Empty<ChannelOverrun>();

    /// <summary>"shift" (the whole evening moves), "next" (only the next show) or "joins" (the next show joins in progress: no change).</summary>
    public string OverrunDefaultChannel { get; set; } = ChannelShift;

    /// <summary>Guide marks for late games: "affected" (only games and shows with a recording), "all", or "off".</summary>
    public string OverrunGuideMarks { get; set; } = GuideMarksAffected;

    /// <summary>Show the score beside the game clock in the guide (off: no spoilers).</summary>
    public bool OverrunShowScores { get; set; }

    /// <summary>Show the extra time as a dashed estimate before it's decided (off: only once decided).</summary>
    public bool OverrunShowEstimates { get; set; } = true;

    /// <summary>"Keep the normal end" and "Add 15 more minutes" in the guide's programme dialog and JDVR.</summary>
    public bool OverrunGuideButtons { get; set; } = true;

    /// <summary>Title/keyword recording rules, independent of guide ids.</summary>
    public FavouriteRule[] Favourites { get; set; } = Array.Empty<FavouriteRule>();

    /// <summary>
    /// Series recordings (series timer ids) and favourites ("fav:" + rule id), highest priority first.
    /// </summary>
    public string[] SeriesPriority { get; set; } = Array.Empty<string>();
}

/// <summary>Where one kind of problem is sent.</summary>
/// <summary>One tuner device's inputs, overriding Jellyfin's tuner count for it.</summary>
public class TunerDeviceSetting
{
    /// <summary>Jellyfin's tuner host id.</summary>
    public string HostId { get; set; } = string.Empty;

    /// <summary>Inputs to plan with (0 = Jellyfin's tuner count; -1 = no limit).</summary>
    public int Inputs { get; set; }
}

public class AlertRoute
{
    public string Kind { get; set; } = string.Empty;

    public bool Tv { get; set; }

    public bool Phone { get; set; }

    public bool Email { get; set; }

    /// <summary>The page's starting choice for a kind.</summary>
    public static AlertRoute Default(string kind) => new()
    {
        Kind = kind,
        Tv = kind != Alerts.AlertKinds.Guide,
        Phone = true,
        Email = false
    };
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

    /// <summary>
    /// Switched off until this time (UTC), then on again by itself; airings from then on are still
    /// scheduled meanwhile. Null with <see cref="Enabled"/> off: off until switched on.
    /// </summary>
    public DateTime? OffUntil
    {
        get => _offUntil;
        // Kept in UTC: the settings file's XML reader hands back local times, and DateTime compares
        // ignore the kind.
        set => _offUntil = value is not { } v ? null
            : v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime();
    }

    private DateTime? _offUntil;

    /// <summary>Whether an airing starting at <paramref name="startUtc"/> is recorded under this rule.</summary>
    public bool ActiveFor(DateTime startUtc) => Enabled || (OffUntil is { } until && startUtc >= until);

    /// <summary>On, or off only for a while (it still schedules what airs after that).</summary>
    public bool IsScheduling() => Enabled || OffUntil.HasValue;
}

/// <summary>Fixed extra time for one sport.</summary>
public class SportExtra
{
    public string Kind { get; set; } = string.Empty;

    public int Minutes { get; set; }

    /// <summary>
    /// How long the channel stays on the game after the final whistle (postgame interviews, other
    /// scores) before the next show starts; null = this sport's default (<see cref="DefaultPostgame"/>).
    /// </summary>
    public int? PostgameMinutes { get; set; }

    /// <summary>Postgame coverage by sport: NBC's Sunday Night Football ran 24 minutes after the final (2026-11-15).</summary>
    public static int DefaultPostgame(string kind) => kind switch
    {
        Overruns.SportKinds.Nfl => 25,
        Overruns.SportKinds.CollegeFootball => 15,
        _ => 10
    };

    public static SportExtra[] Defaults() => new[]
    {
        new SportExtra { Kind = Overruns.SportKinds.Nfl, Minutes = 45 },
        new SportExtra { Kind = Overruns.SportKinds.CollegeFootball, Minutes = 60 },
        new SportExtra { Kind = Overruns.SportKinds.Basketball, Minutes = 30 },
        new SportExtra { Kind = Overruns.SportKinds.Hockey, Minutes = 30 },
        new SportExtra { Kind = Overruns.SportKinds.Baseball, Minutes = 60 },
        new SportExtra { Kind = Overruns.SportKinds.Soccer, Minutes = 15 },
        new SportExtra { Kind = Overruns.SportKinds.Other, Minutes = 30 },
    };
}

/// <summary>What a late game does to the shows after it on one channel.</summary>
public class ChannelOverrun
{
    /// <summary>Library channel id ("N" format).</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>"shift", "next" or "joins".</summary>
    public string Mode { get; set; } = PluginConfiguration.ChannelShift;
}
