using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DvrSchedule.Alerts;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using Jellyfin.Plugin.DvrSchedule.Schedule;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Overruns;

/// <summary>
/// When a live game runs late, the shows after it on that channel start late too. Jellyfin fixes a
/// recording's length the moment it starts (it reads the end padding once, then never again), so
/// this decides shortly before each such recording starts: if the game isn't over, the end padding
/// grows by how long the game still needs (from ESPN's scoreboard) plus a margin. A recording of the
/// game itself gets the sport's fixed extra time, and if the game is still going at its end, a
/// follow-up recording on the same channel that is joined onto it afterwards (<see cref="FollowUpJoiner"/>).
/// </summary>
public sealed class OverrunWatcher : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(45);

    /// <summary>Decide this long before a recording starts (the last moment Jellyfin takes a change is the start).</summary>
    private static readonly TimeSpan DecideBefore = TimeSpan.FromMinutes(3);

    /// <summary>A game recording gets its follow-up this long before it ends.</summary>
    private static readonly TimeSpan FollowUpBefore = TimeSpan.FromMinutes(4);

    /// <summary>The follow-up starts this long before the game's recording ends, so nothing is lost at the handover.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromSeconds(30);

    /// <summary>"Whole evening shifts": recordings this long after the game still move with it.</summary>
    private static readonly TimeSpan EveningLength = TimeSpan.FromHours(6);

    private readonly ILogger<OverrunWatcher> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly IRecordingsManager _recordings;
    private readonly Scoreboard _scoreboard;
    private readonly OverrunStore _store;
    private readonly ScheduleManager _manager;
    private readonly ActiveRecordings _active;
    private readonly AlertCenter _center;
    private readonly FollowUpJoiner _joiner;

    // Guide game entry id → what the scoreboard says about it.
    private readonly ConcurrentDictionary<Guid, Track> _games = new();
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OverrunWatcher(
        ILogger<OverrunWatcher> logger,
        IServiceProvider serviceProvider,
        ILibraryManager libraryManager,
        IRecordingsManager recordings,
        Scoreboard scoreboard,
        OverrunStore store,
        ScheduleManager manager,
        ActiveRecordings active,
        AlertCenter center,
        FollowUpJoiner joiner)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _libraryManager = libraryManager;
        _recordings = recordings;
        _scoreboard = scoreboard;
        _store = store;
        _manager = manager;
        _active = active;
        _center = center;
        _joiner = joiner;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private ILiveTvManager LiveTv => _serviceProvider.GetRequiredService<ILiveTvManager>();

    /// <summary>The recordings watched on the last check, for the settings page and JDVR.</summary>
    public IReadOnlyList<Watch> Watches { get; private set; } = Array.Empty<Watch>();

    public DateTime? LastCheckUtc { get; private set; }

    public IReadOnlyList<string> Recent => _recent.Reverse().ToList();

    public Scoreboard Board => _scoreboard;

    /// <summary>One recording the watch looks at.</summary>
    public sealed class Watch
    {
        public string TimerId { get; set; } = string.Empty;

        public Guid ProgramId { get; set; }

        public string Title { get; set; } = string.Empty;

        public Guid ChannelId { get; set; }

        public string ChannelName { get; set; } = string.Empty;

        public DateTime StartUtc { get; set; }

        public DateTime EndUtc { get; set; }

        /// <summary>"after" (a show after a game), "game" (a recording of the game), "followup".</summary>
        public string Role { get; set; } = "after";

        public string GameTitle { get; set; } = string.Empty;

        public DateTime GameStartUtc { get; set; }

        public DateTime GameEndUtc { get; set; }

        /// <summary>The scoreboard's words ("Q4 2:14", "Final"), or why there are none.</summary>
        public string GameStatus { get; set; } = string.Empty;

        /// <summary>"shift", "next", "joins".</summary>
        public string ChannelMode { get; set; } = PluginConfiguration.ChannelShift;

        /// <summary>
        /// "watching" (not decided yet), "ontime", "extend" (extra time added, or would be in preview),
        /// "capped", "joins" (no change: the channel joins in progress), "toolate" (already recording),
        /// "followup" (a follow-up recording is set), "nofollowup" (one was needed but couldn't be set).
        /// </summary>
        public string State { get; set; } = "watching";

        public int ExtraMinutes { get; set; }

        public int LaterStartMinutes { get; set; }

        /// <summary>Whether the change is real (active mode) or only shown (preview).</summary>
        public bool Applied { get; set; }

        public string Note { get; set; } = string.Empty;

        /// <summary>The game's guide entry.</summary>
        public Guid GameProgramId { get; set; }

        /// <summary>Extra time it would get so far, before the decision (0 = none or not known).</summary>
        public int EstimateMinutes { get; set; }

        /// <summary>Minutes added by hand.</summary>
        public int UserExtraMinutes { get; set; }

        /// <summary>Someone chose "Keep the normal end".</summary>
        public bool Kept { get; set; }

        /// <summary>Already recording: its end can't change any more.</summary>
        public bool Recording { get; set; }
    }

    private sealed class Track
    {
        public Game? Game { get; set; }

        public bool Looked { get; set; }

        public DateTime LookedUtc { get; set; }

        public DateTime? LiveSeenUtc { get; set; }

        public DateTime? FinalSeenUtc { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(75), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Sports overrun check failed");
                }

                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        var cfg = Config;
        if (cfg.OverrunMode == PluginConfiguration.OverrunOff)
        {
            Watches = Array.Empty<Watch>();
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _scoreboard.MaxAge = TimeSpan.FromMinutes(Math.Clamp(cfg.OverrunPollMinutes, 1, 10));
            var now = DateTime.UtcNow;
            var active = cfg.OverrunMode == PluginConfiguration.ModeActive;
            var buffers = _manager.LiveBufferTimerIds();
            var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items
                .Where(t => t.Status is RecordingStatus.New or RecordingStatus.InProgress
                    && !buffers.Contains(t.Id)
                    && t.StartDate < now.AddHours(14) && t.EndDate > now.AddMinutes(-5))
                .OrderBy(t => t.StartDate)
                .ToList();

            var watches = new List<Watch>();
            foreach (var t in timers)
            {
                if (!Guid.TryParse(t.ProgramId, out var programId) || _libraryManager.GetItemById(programId) is not LiveTvProgram program)
                {
                    continue;
                }

                var chain = _store.ChainWith(t.Id);
                var w = chain is not null && chain.FirstTimerId != t.Id
                    ? await FollowUpWatchAsync(t, program, chain, ct).ConfigureAwait(false)
                    : IsGame(program) ? await GameWatchAsync(t, program, cfg, ct).ConfigureAwait(false)
                    : await AfterGameWatchAsync(t, program, cfg, ct).ConfigureAwait(false);
                if (w is null)
                {
                    continue;
                }

                if (w.Role == "after")
                {
                    await DecideAfterAsync(t, w, cfg, active, now, ct).ConfigureAwait(false);
                }
                else
                {
                    await DecideGameAsync(t, program, w, chain, cfg, active, now, ct).ConfigureAwait(false);
                }

                watches.Add(w);
            }

            if (cfg.OverrunGuideMarks == PluginConfiguration.GuideMarksAll)
            {
                // Every game on now (or just over), for the guide's marks.
                foreach (var g in OnAirGames(now))
                {
                    await LookUpAsync(g, ct).ConfigureAwait(false);
                }
            }

            Watches = watches;
            LastCheckUtc = now;
            ForgetOldGames(now);
        }
        finally
        {
            _lock.Release();
        }

        await _joiner.JoinReadyAsync(ct).ConfigureAwait(false);
    }

    // ---- what a recording is -------------------------------------------------------------

    /// <summary>A sports entry in the guide (the flag, or a sport genre).</summary>
    public static bool IsSports(LiveTvProgram p) =>
        p.IsSports || (p.Genres ?? Array.Empty<string>()).Any(g =>
            g.Contains("sport", StringComparison.OrdinalIgnoreCase) || g.Contains("football", StringComparison.OrdinalIgnoreCase)
            || g.Contains("basketball", StringComparison.OrdinalIgnoreCase) || g.Contains("hockey", StringComparison.OrdinalIgnoreCase)
            || g.Contains("baseball", StringComparison.OrdinalIgnoreCase) || g.Contains("soccer", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A sports entry that is a game (two teams: "49ers at Cowboys", "Arsenal vs. Chelsea"), not a
    /// studio or postgame show, which only fills time and doesn't run late.
    /// </summary>
    public static bool IsGame(LiveTvProgram p) => IsSports(p) && LooksLikeGame(p.Name + " " + p.EpisodeTitle);

    public static bool LooksLikeGame(string text)
    {
        var t = " " + text.ToLowerInvariant() + " ";
        return t.Contains(" at ", StringComparison.Ordinal) || t.Contains(" vs", StringComparison.Ordinal)
            || t.Contains(" v ", StringComparison.Ordinal) || t.Contains(" @ ", StringComparison.Ordinal);
    }

    private static DateTime EndOf(LiveTvProgram p) => p.EndDate ?? p.StartDate.AddHours(1);

    private List<LiveTvProgram> ChannelPrograms(Guid channelId, DateTime from, DateTime to) =>
        _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.TvProgram },
                ChannelIds = new[] { channelId },
                MinEndDate = from,
                MaxStartDate = to
            })
            .OfType<LiveTvProgram>()
            .OrderBy(p => p.StartDate)
            .ToList();

    private static string ChannelMode(PluginConfiguration cfg, Guid channelId) =>
        cfg.OverrunChannels.FirstOrDefault(c => Guid.TryParse(c.ChannelId, out var id) && id == channelId)?.Mode ?? cfg.OverrunDefaultChannel;

    private string ChannelName(Guid id) => _libraryManager.GetItemById(id)?.Name ?? string.Empty;

    private Watch NewWatch(TimerInfoDto t, LiveTvProgram program, string role) => new()
    {
        TimerId = t.Id,
        ProgramId = program.Id,
        Title = t.Name ?? program.Name,
        ChannelId = program.ChannelId,
        ChannelName = t.ChannelName ?? ChannelName(program.ChannelId),
        StartUtc = t.StartDate,
        EndUtc = t.EndDate,
        Role = role,
        Recording = t.Status == RecordingStatus.InProgress || _recordings.GetActiveRecordingPath(t.Id) is not null
    };

    // A show after a game on its channel: the latest game that ended within the look-back time.
    private async Task<Watch?> AfterGameWatchAsync(TimerInfoDto t, LiveTvProgram program, PluginConfiguration cfg, CancellationToken ct)
    {
        var lookBack = TimeSpan.FromHours(Math.Clamp(cfg.OverrunLookBackHours, 1, 8));
        var before = ChannelPrograms(program.ChannelId, t.StartDate - lookBack, t.StartDate.AddMinutes(-1))
            .Where(p => p.StartDate < t.StartDate && p.Id != program.Id)
            .ToList();
        var game = before.Where(IsGame).OrderByDescending(p => p.StartDate).FirstOrDefault();
        if (game is null)
        {
            return null;
        }

        // The channel's schedule restarts at the first non-sports entry after the game: postgame
        // shows in between soak up a short overrun.
        var anchor = before.Where(p => p.StartDate >= EndOf(game).AddMinutes(-1) && !IsSports(p)).Select(p => (DateTime?)p.StartDate).FirstOrDefault()
            ?? t.StartDate;
        var mode = ChannelMode(cfg, program.ChannelId);
        if (mode == PluginConfiguration.ChannelNext && t.StartDate > anchor.AddMinutes(1))
        {
            return null; // only the first show after the game moves
        }

        if (t.StartDate > anchor + EveningLength)
        {
            return null;
        }

        var w = NewWatch(t, program, "after");
        w.GameTitle = Describe(game);
        w.GameProgramId = game.Id;
        w.GameStartUtc = game.StartDate;
        w.GameEndUtc = EndOf(game);
        w.ChannelMode = mode;
        w.Note = anchor < t.StartDate.AddMinutes(-1) ? "Evening restarts at " + Local(anchor) : string.Empty;
        _anchors[t.Id] = (anchor, game.Id);
        await LookUpAsync(game, ct).ConfigureAwait(false);
        return w;
    }

    private readonly ConcurrentDictionary<string, (DateTime Anchor, Guid GameId)> _anchors = new();

    private async Task<Watch?> GameWatchAsync(TimerInfoDto t, LiveTvProgram program, PluginConfiguration cfg, CancellationToken ct)
    {
        var w = NewWatch(t, program, "game");
        w.GameTitle = Describe(program);
        w.GameProgramId = program.Id;
        w.GameStartUtc = program.StartDate;
        w.GameEndUtc = EndOf(program);
        w.ChannelMode = ChannelMode(cfg, program.ChannelId);
        await LookUpAsync(program, ct).ConfigureAwait(false);
        return w;
    }

    private async Task<Watch?> FollowUpWatchAsync(TimerInfoDto t, LiveTvProgram program, OverrunStore.GameChain chain, CancellationToken ct)
    {
        var w = NewWatch(t, program, "followup");
        w.GameTitle = chain.Title;
        w.State = "followup";
        var first = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.TvProgram }, ChannelIds = new[] { chain.ChannelId }, MinEndDate = t.StartDate.AddHours(-8), MaxStartDate = t.StartDate })
            .OfType<LiveTvProgram>().Where(IsGame).OrderByDescending(p => p.StartDate).FirstOrDefault();
        if (first is not null)
        {
            w.GameProgramId = first.Id;
            w.GameStartUtc = first.StartDate;
            w.GameEndUtc = EndOf(first);
            await LookUpAsync(first, ct).ConfigureAwait(false);
            _followGame[t.Id] = first.Id;
        }

        w.Note = "Follow-up of " + chain.Title + "; joined onto it when the game is over";
        return w;
    }

    private readonly ConcurrentDictionary<string, Guid> _followGame = new();

    private static string Describe(LiveTvProgram p) =>
        string.IsNullOrWhiteSpace(p.EpisodeTitle) ? p.Name : p.Name + ": " + p.EpisodeTitle;

    // ---- the scoreboard ------------------------------------------------------------------

    private async Task LookUpAsync(LiveTvProgram game, CancellationToken ct)
    {
        var track = _games.GetOrAdd(game.Id, _ => new Track());
        var now = DateTime.UtcNow;
        // Only while the game could be on or just over; a game listed tomorrow needs no score yet.
        if (now < game.StartDate.AddMinutes(-20) || track.Game?.IsFinal == true)
        {
            return;
        }

        if (track.Game is null)
        {
            if (track.Looked && now - track.LookedUtc < TimeSpan.FromMinutes(10))
            {
                return; // no match last time; look again now and then (the teams may be added later)
            }

            track.Looked = true;
            track.LookedUtc = now;
            track.Game = await _scoreboard.FindAsync(game.Name, game.EpisodeTitle, game.Genres ?? Array.Empty<string>(), game.StartDate, ct).ConfigureAwait(false);
            if (track.Game is not null)
            {
                Note($"Watching {track.Game.Name} ({track.Game.League}) for {Describe(game)}");
            }
        }
        else
        {
            track.Game = await _scoreboard.RefreshAsync(track.Game, ct).ConfigureAwait(false) ?? track.Game;
        }

        if (track.Game is { } g)
        {
            if (g.IsLive)
            {
                track.LiveSeenUtc = now;
            }
            else if (g.IsFinal && track.FinalSeenUtc is null && track.LiveSeenUtc is not null)
            {
                // Seen live, now final: it ended within the last poll.
                track.FinalSeenUtc = now - (_scoreboard.MaxAge / 2);
                Note($"{g.Name} final at {Local(track.FinalSeenUtc.Value)}");
            }
        }
    }

    private void ForgetOldGames(DateTime now)
    {
        foreach (var (id, _) in _games.Where(kv => kv.Value.LookedUtc < now.AddHours(-16) && kv.Value.LookedUtc > DateTime.MinValue).ToList())
        {
            _games.TryRemove(id, out _);
        }
    }

    // Coverage after the final whistle before the channel's next show (setting, or the sport's default).
    private static int Postgame(PluginConfiguration cfg, string kind) =>
        Math.Clamp(cfg.OverrunSportMinutes.FirstOrDefault(s => s.Kind == kind)?.PostgameMinutes ?? SportExtra.DefaultPostgame(kind), 0, 120);

    /// <summary>
    /// When the channel should be back on its schedule: the game's end plus the network's postgame
    /// coverage, less what the guide already gives to postgame shows between the game and [anchor].
    /// </summary>
    private (DateTime? End, bool Known, string Status, string Kind) BroadcastEnd(Guid gameProgramId, DateTime? anchor, PluginConfiguration cfg, DateTime now)
    {
        var r = GameEnd(gameProgramId, now);
        if (r.End is not { } end || !r.Known)
        {
            return r;
        }

        var listed = _libraryManager.GetItemById(gameProgramId) is LiveTvProgram p ? EndOf(p) : end;
        var guideGives = anchor is { } a ? Math.Max(0, (a - listed).TotalMinutes) : 0;
        var post = Math.Max(0, Postgame(cfg, r.Kind) - guideGives);
        return (end.AddMinutes(post), true, r.Status + (post > 0 ? $" · +{post:0} min postgame" : string.Empty), r.Kind);
    }

    private int FixedMinutes(PluginConfiguration cfg, string kind) =>
        cfg.OverrunSportMinutes.FirstOrDefault(s => s.Kind == kind)?.Minutes
        ?? cfg.OverrunSportMinutes.FirstOrDefault(s => s.Kind == SportKinds.Other)?.Minutes ?? 30;

    /// <summary>
    /// When the game's broadcast will be over (UTC), or null when nothing can tell; and the words
    /// for the settings page.
    /// </summary>
    private (DateTime? End, bool Known, string Status, string Kind) GameEnd(Guid gameProgramId, DateTime now)
    {
        if (!_games.TryGetValue(gameProgramId, out var track) || track.Game is not { } g)
        {
            return (null, false, "Not on the scoreboard", SportKinds.Other);
        }

        if (g.IsFinal)
        {
            return track.FinalSeenUtc is { } f ? (f, true, "Final, " + Local(f), g.Kind) : (null, false, "Final", g.Kind);
        }

        if (Estimator.MinutesLeft(g) is { } left)
        {
            return (now.AddMinutes(left), true, Estimator.Describe(g) + " · about " + Math.Round(left) + " min left", g.Kind);
        }

        return (null, false, Estimator.Describe(g), g.Kind);
    }

    // ---- shows after a game --------------------------------------------------------------

    private async Task DecideAfterAsync(TimerInfoDto t, Watch w, PluginConfiguration cfg, bool active, DateTime now, CancellationToken ct)
    {
        if (!_anchors.TryGetValue(t.Id, out var a))
        {
            return;
        }

        var (end, known, status, kind) = BroadcastEnd(a.GameId, a.Anchor, cfg, now);
        w.GameStatus = status;
        var done = _store.ChangeFor(t.Id, t.StartDate);
        if (done is not null)
        {
            w.ExtraMinutes = done.ExtraMinutes;
            w.LaterStartMinutes = done.LaterStartMinutes;
            w.Applied = true;
            w.State = done.ExtraMinutes > 0 ? "extend" : "ontime";
            w.UserExtraMinutes = done.UserExtraMinutes;
            if (done.UserKept)
            {
                w.State = "kept";
                w.ExtraMinutes = 0;
                w.Kept = true;
                w.Note = "Kept to its normal end by hand" + (done.UserExtraMinutes > 0 ? $", +{done.UserExtraMinutes} min added by hand" : string.Empty);
                return;
            }
        }

        if (w.ChannelMode == PluginConfiguration.ChannelJoins)
        {
            w.State = "joins";
            w.Note = "This channel joins the next show in progress: no change";
            return;
        }

        var fire = t.StartDate.AddSeconds(-Math.Max(0, t.PrePaddingSeconds));
        if (t.Status == RecordingStatus.InProgress || _recordings.GetActiveRecordingPath(t.Id) is not null)
        {
            if (done is null)
            {
                w.State = "toolate";
                w.Note = "Already recording: Jellyfin doesn't take a new end once a recording has started";
            }

            return;
        }

        if (now < fire - DecideBefore)
        {
            w.State = done is null ? "watching" : w.State;
            if (end is { } e && known)
            {
                var guess = (int)Math.Ceiling(Math.Max(0, (e - a.Anchor).TotalMinutes + cfg.OverrunMarginMinutes));
                w.EstimateMinutes = (e - a.Anchor).TotalMinutes > 0 ? Math.Min(guess, Math.Clamp(cfg.OverrunCapMinutes, 5, 360)) : 0;
                w.Note = guess > 0 ? $"So far it would get +{guess} min; decided {DecideBefore.TotalMinutes:0} min before it starts" : "So far on time; decided shortly before it starts";
            }

            return;
        }

        // Decide (again, until it starts: an estimate only ever grows the extra time).
        int extra;
        var reason = "game ran late";
        if (end is { } gameEnd)
        {
            extra = (int)Math.Ceiling(Math.Max(0, (gameEnd - a.Anchor).TotalMinutes + cfg.OverrunMarginMinutes));
            if ((gameEnd - a.Anchor).TotalMinutes <= 0 && extra > 0 && w.GameStatus.StartsWith("Final", StringComparison.Ordinal))
            {
                extra = 0; // over before the show's slot: nothing to add
            }
        }
        else if (w.GameStatus == "Final" && a.Anchor > now)
        {
            extra = 0; // over already and the show hasn't started: on time
        }
        else
        {
            extra = FixedMinutes(cfg, kind);
            reason = "game may run late";
        }

        var cap = Math.Clamp(cfg.OverrunCapMinutes, 5, 360);
        var wanted = extra;
        extra = Math.Min(extra, cap);
        if (done is not null && extra <= done.ExtraMinutes)
        {
            return; // already given at least this much
        }

        var later = 0;
        if (cfg.OverrunMoveStart && end is not null && known && extra - (2 * cfg.OverrunMarginMinutes) > 5)
        {
            later = extra - (2 * cfg.OverrunMarginMinutes);
        }

        var origPre = done?.OriginalPrePadding ?? t.PrePaddingSeconds;
        var origPost = done?.OriginalPostPadding ?? t.PostPaddingSeconds;
        w.ExtraMinutes = extra;
        w.LaterStartMinutes = later;
        w.State = wanted > cap ? "capped" : extra > 0 ? "extend" : "ontime";
        w.Note = extra > 0
            ? $"End moved to {Local(t.EndDate.AddSeconds(cfg.OverrunKeepPadding ? origPost : 0).AddMinutes(extra))}" + (later > 0 ? $", start to {Local(t.StartDate.AddMinutes(later))}" : string.Empty) + (wanted > cap ? $" (wanted +{wanted} min; capped at {cap})" : string.Empty)
            : "The game is over in time";
        if (!active)
        {
            w.Note = "Preview: " + w.Note;
            return;
        }

        if (extra > 0)
        {
            t.PostPaddingSeconds = (cfg.OverrunKeepPadding ? origPost + (extra * 60) : Math.Max(origPost, extra * 60)) + ((done?.UserExtraMinutes ?? 0) * 60);
            if (later > 0)
            {
                t.PrePaddingSeconds = -(later * 60);
            }

            await LiveTv.UpdateTimer(t, ct).ConfigureAwait(false);
            Note($"{w.Title} ({w.ChannelName}): end +{extra} min ({w.GameTitle}: {w.GameStatus})" + (later > 0 ? $", start +{later} min" : string.Empty));
        }

        w.Applied = true;
        _store.Save(new OverrunStore.Change
        {
            TimerId = t.Id,
            StartUtc = t.StartDate,
            Title = w.Title,
            OriginalPrePadding = origPre,
            OriginalPostPadding = origPost,
            ExtraMinutes = extra,
            LaterStartMinutes = later,
            Reason = reason,
            ChangedUtc = DateTime.UtcNow,
            UserExtraMinutes = done?.UserExtraMinutes ?? 0
        });

        if (wanted > cap)
        {
            await AlertAsync(cfg, "overrun-cap:" + t.Id, $"{w.Title} may miss its end", $"{w.GameTitle} is running late ({w.GameStatus}). {w.Title} on {w.ChannelName} wanted {wanted} extra minutes; it got the {cap}-minute cap.", w, ct).ConfigureAwait(false);
        }
    }

    // ---- the game's own recording ----------------------------------------------------------

    private async Task DecideGameAsync(TimerInfoDto t, LiveTvProgram program, Watch w, OverrunStore.GameChain? chain, PluginConfiguration cfg, bool active, DateTime now, CancellationToken ct)
    {
        var gameId = w.Role == "followup" && _followGame.TryGetValue(t.Id, out var fg) ? fg : program.Id;
        var (end, known, status, kind) = GameEnd(gameId, now);
        w.GameStatus = status;
        var done = _store.ChangeFor(t.Id, t.StartDate);
        if (done is not null)
        {
            w.ExtraMinutes = done.ExtraMinutes;
            w.Applied = true;
            w.State = w.Role == "followup" ? "followup" : "extend";
            w.UserExtraMinutes = done.UserExtraMinutes;
            w.Kept = done.UserKept;
        }

        var recording = t.Status == RecordingStatus.InProgress || _recordings.GetActiveRecordingPath(t.Id) is not null;
        if (recording && _recordings.GetActiveRecordingPath(t.Id) is { } path)
        {
            // Note the files of a game with follow-ups, for the join.
            var c = _store.ChainWith(t.Id);
            if (c is not null && !c.Paths.ContainsKey(t.Id))
            {
                c.Paths[t.Id] = path;
                _store.Save(c);
            }
        }

        // Before it starts: the sport's fixed extra time, so overtime is usually inside the first recording.
        var fire = t.StartDate.AddSeconds(-Math.Max(0, t.PrePaddingSeconds));
        if (!recording && w.Role == "game")
        {
            var fixedExtra = Math.Min(FixedMinutes(cfg, kind == SportKinds.Other ? Guess(program) : kind), Math.Clamp(cfg.OverrunCapMinutes, 5, 360));
            if (done?.UserKept == true)
            {
                w.State = "kept";
                w.ExtraMinutes = 0;
                w.Note = "Kept to its normal end by hand";
                return;
            }

            var given = done is not null && done.ExtraMinutes > 0;
            w.EstimateMinutes = given ? 0 : fixedExtra;
            if (!given && fixedExtra > 0 && now >= fire - DecideBefore)
            {
                w.ExtraMinutes = fixedExtra;
                w.State = "extend";
                w.Note = $"Game recording: +{fixedExtra} min for overtime" + (cfg.OverrunFollowUpMinutes > 0 ? "; a follow-up if it's still going then" : string.Empty);
                if (!active)
                {
                    w.Note = "Preview: " + w.Note;
                    return;
                }

                var origPost = done?.OriginalPostPadding ?? t.PostPaddingSeconds;
                var user = done?.UserExtraMinutes ?? 0;
                t.PostPaddingSeconds = origPost + ((fixedExtra + user) * 60);
                await LiveTv.UpdateTimer(t, ct).ConfigureAwait(false);
                w.Applied = true;
                _store.Save(new OverrunStore.Change { TimerId = t.Id, StartUtc = t.StartDate, Title = w.Title, OriginalPrePadding = done?.OriginalPrePadding ?? t.PrePaddingSeconds, OriginalPostPadding = origPost, ExtraMinutes = fixedExtra, Reason = "overtime", ChangedUtc = DateTime.UtcNow, UserExtraMinutes = user });
                Note($"{w.Title} ({w.ChannelName}): game recording +{fixedExtra} min for overtime");
            }
            else if (!given)
            {
                w.State = "watching";
                w.Note = $"Will get +{fixedExtra} min for overtime when it starts";
            }

            return;
        }

        if (!recording)
        {
            return;
        }

        // Near its end with the game still on: a follow-up recording.
        var recEnd = t.EndDate.AddSeconds(t.PostPaddingSeconds);
        var mine = _store.ChainWith(t.Id);
        if (mine is not null && mine.TimerIds.IndexOf(t.Id) < mine.TimerIds.Count - 1)
        {
            w.State = "followup";
            w.Note = "Continues in a follow-up recording";
            return;
        }

        if (cfg.OverrunFollowUpMinutes <= 0 || now < recEnd - FollowUpBefore || now > recEnd)
        {
            if (end is { } e && known && e > recEnd && cfg.OverrunFollowUpMinutes > 0)
            {
                w.Note = $"Game may outlast this recording (ends {Local(recEnd)}): a follow-up is set {FollowUpBefore.TotalMinutes:0} min before";
            }

            return;
        }

        if (!_games.TryGetValue(gameId, out var track) || track.Game is not { IsFinal: false } game)
        {
            return; // over, or not on the scoreboard (then the fixed extra was all)
        }

        var left = Estimator.MinutesLeft(game) ?? FixedMinutes(cfg, game.Kind);
        var target = now.AddMinutes(left + cfg.OverrunMarginMinutes);
        if (target <= recEnd.AddMinutes(2))
        {
            return; // it'll be over in time
        }

        var firstId = mine?.FirstTimerId ?? t.Id;
        var limit = (mine?.FirstEndUtc ?? recEnd).AddMinutes(cfg.OverrunFollowUpMinutes);
        if (target > limit)
        {
            target = limit;
        }

        if (target <= recEnd.AddMinutes(2))
        {
            w.State = "nofollowup";
            w.Note = $"Follow-ups used up ({cfg.OverrunFollowUpMinutes} min)";
            await AlertAsync(cfg, "overrun-follow:" + firstId, $"{w.GameTitle} may be cut off", $"The game is still going ({w.GameStatus}) and its follow-up recordings reached the {cfg.OverrunFollowUpMinutes}-minute limit.", w, ct).ConfigureAwait(false);
            return;
        }

        var next = ChannelPrograms(program.ChannelId, recEnd, recEnd.AddMinutes(1))
            .FirstOrDefault(p => p.StartDate <= recEnd.AddMinutes(1) && EndOf(p) > recEnd);
        if (next is null)
        {
            w.State = "nofollowup";
            w.Note = "No guide entry after " + Local(recEnd) + " to record the rest under";
            await AlertAsync(cfg, "overrun-follow:" + firstId, $"{w.GameTitle} may be cut off", $"The game is still going ({w.GameStatus}) but the guide has nothing on {w.ChannelName} after {Local(recEnd)} to record the rest under.", w, ct).ConfigureAwait(false);
            return;
        }

        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;
        var existing = timers.FirstOrDefault(x => x.ProgramId == next.Id.ToString("N") && x.Status is RecordingStatus.New or RecordingStatus.InProgress);
        if (existing is not null)
        {
            w.State = "followup";
            w.Note = $"The rest is in the recording of {existing.Name} (already scheduled), which gets the game's extra time too";
            return;
        }

        // The follow-up needs an input on the channel's own tuner device (it already has one if
        // the game's recording is still holding the channel).
        var tuners = await _serviceProvider.GetRequiredService<TunerLayouts>().GetAsync(ct).ConfigureAwait(false);
        var pool = tuners.PoolOf(next.ChannelId);
        var capacity = tuners.InputsOf(pool);
        var busyChannels = _active.Current().Select(a => a.ChannelId).Where(id => tuners.PoolOf(id) == pool).ToHashSet();
        if (capacity > 0 && !busyChannels.Contains(next.ChannelId) && busyChannels.Count >= capacity)
        {
            w.State = "nofollowup";
            w.Note = "No free tuner for the follow-up";
            await AlertAsync(cfg, "overrun-follow:" + firstId, $"{w.GameTitle} may be cut off", $"The game is still going ({w.GameStatus}) and every tuner is busy, so the follow-up recording couldn't start. The recording ends {Local(recEnd)}.", w, ct).ConfigureAwait(false);
            return;
        }

        var start = recEnd - Overlap;
        var pre = (int)Math.Max(0, Math.Ceiling((next.StartDate - start).TotalSeconds));
        var post = (int)Math.Ceiling((target - EndOf(next)).TotalSeconds);
        w.State = "followup";
        w.Note = $"Follow-up {Local(start)}–{Local(target)} under “{next.Name}”, joined onto this recording afterwards";
        if (!active)
        {
            w.Note = "Preview: " + w.Note;
            return;
        }

        await _manager.CreateTimerAsync(next.Id, pre, post, ct).ConfigureAwait(false);
        var created = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items
            .FirstOrDefault(x => x.ProgramId == next.Id.ToString("N") && x.Status == RecordingStatus.New);
        if (created is null)
        {
            w.State = "nofollowup";
            w.Note = "Jellyfin didn't take the follow-up recording";
            return;
        }

        var c2 = mine ?? new OverrunStore.GameChain
        {
            FirstTimerId = t.Id,
            Title = w.Title,
            ChannelId = program.ChannelId,
            GameId = game.Id,
            League = game.League,
            CreatedUtc = DateTime.UtcNow,
            FirstEndUtc = recEnd,
            TimerIds = new List<string> { t.Id }
        };
        if (_recordings.GetActiveRecordingPath(t.Id) is { } p2)
        {
            c2.Paths[t.Id] = p2;
        }

        c2.TimerIds.Add(created.Id);
        c2.State = "pending";
        _store.Save(c2);
        _store.Save(new OverrunStore.Change { TimerId = created.Id, StartUtc = created.StartDate, Title = created.Name ?? next.Name, OriginalPrePadding = pre, OriginalPostPadding = post, ExtraMinutes = 0, Reason = "follow-up", ChangedUtc = DateTime.UtcNow });
        Note($"{w.Title}: follow-up recording {Local(start)}–{Local(target)} ({game.Name}: {Estimator.Describe(game)})");
    }

    // A game the scoreboard doesn't know: its kind from the guide's words.
    private static string Guess(LiveTvProgram p)
    {
        var t = (p.Name + " " + p.EpisodeTitle + " " + string.Join(' ', p.Genres ?? Array.Empty<string>())).ToLowerInvariant();
        return t.Contains("college football", StringComparison.Ordinal) || t.Contains("ncaa football", StringComparison.Ordinal) ? SportKinds.CollegeFootball
            : t.Contains("nfl", StringComparison.Ordinal) || t.Contains("football", StringComparison.Ordinal) ? SportKinds.Nfl
            : t.Contains("basketball", StringComparison.Ordinal) || t.Contains("nba", StringComparison.Ordinal) ? SportKinds.Basketball
            : t.Contains("hockey", StringComparison.Ordinal) || t.Contains("nhl", StringComparison.Ordinal) ? SportKinds.Hockey
            : t.Contains("baseball", StringComparison.Ordinal) || t.Contains("mlb", StringComparison.Ordinal) ? SportKinds.Baseball
            : t.Contains("soccer", StringComparison.Ordinal) ? SportKinds.Soccer
            : SportKinds.Other;
    }

    private List<LiveTvProgram> OnAirGames(DateTime now) =>
        _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.TvProgram },
                MinEndDate = now.AddHours(-3),
                MaxStartDate = now.AddMinutes(15)
            })
            .OfType<LiveTvProgram>()
            .Where(IsGame)
            .ToList();

    // ---- the guide ----------------------------------------------------------------------------

    /// <summary>
    /// What the guides (jellyfin-web and JDVR) mark: games running late, recordings given extra time
    /// (or about to be), and, with "all", the shows a late game pushes back.
    /// </summary>
    public GuideMarks Guide(bool canChange)
    {
        var cfg = Config;
        var marks = new GuideMarks
        {
            Marks = cfg.OverrunMode == PluginConfiguration.OverrunOff ? PluginConfiguration.OverrunOff : cfg.OverrunGuideMarks,
            ShowScores = cfg.OverrunShowScores,
            ShowEstimates = cfg.OverrunShowEstimates,
            Buttons = cfg.OverrunGuideButtons && canChange,
            Preview = cfg.OverrunMode != PluginConfiguration.ModeActive
        };
        if (marks.Marks == PluginConfiguration.OverrunOff)
        {
            return marks;
        }

        var now = DateTime.UtcNow;
        var all = marks.Marks == PluginConfiguration.GuideMarksAll;
        var watches = Watches;
        var gameIds = all
            ? _games.Keys.ToHashSet()
            : watches.Where(w => w.GameProgramId != Guid.Empty).Select(w => w.GameProgramId).ToHashSet();

        foreach (var id in gameIds)
        {
            if (!_games.TryGetValue(id, out var track) || track.Game is not { } g || _libraryManager.GetItemById(id) is not LiveTvProgram p)
            {
                continue;
            }

            // On now, or over within the last half hour.
            if (!g.IsLive && !(g.IsFinal && track.FinalSeenUtc is { } f && now - f < TimeSpan.FromMinutes(30)))
            {
                continue;
            }

            var (end, known, _, _) = BroadcastEnd(id, null, cfg, now);
            var listedEnd = EndOf(p);
            var item = new GuideGame
            {
                ProgramId = p.Id.ToString("N"),
                ChannelId = p.ChannelId.ToString("N"),
                ListedStartUtc = p.StartDate,
                ListedEndUtc = listedEnd,
                EstimatedEndUtc = known ? end : null,
                Final = g.IsFinal,
                Clock = g.IsFinal ? "Final" : Estimator.Describe(g),
                Score = cfg.OverrunShowScores ? g.Score : string.Empty
            };
            item.Late = item.EstimatedEndUtc is { } e && e > listedEnd.AddMinutes(2);
            marks.Games.Add(item);

            if (all && item.Late && !g.IsFinal)
            {
                AddShifted(marks, cfg, p, item.EstimatedEndUtc!.Value);
            }
        }

        foreach (var w in watches.Where(w => w.Role != "game" || w.ExtraMinutes > 0 || w.EstimateMinutes > 0 || w.Kept || w.UserExtraMinutes > 0))
        {
            var decided = w.Applied || w.Kept;
            var extra = w.Kept ? 0 : decided ? w.ExtraMinutes : 0;
            // Before the decision, or in preview (decided but not applied): an estimate.
            var estimate = decided || !cfg.OverrunShowEstimates ? 0 : Math.Max(w.EstimateMinutes, w.ExtraMinutes);
            if (extra == 0 && estimate == 0 && w.UserExtraMinutes == 0 && !w.Kept && w.Role != "followup")
            {
                continue; // nothing to show
            }

            marks.Recordings.Add(new GuideRecording
            {
                ProgramId = w.ProgramId.ToString("N"),
                TimerId = w.TimerId,
                ChannelId = w.ChannelId.ToString("N"),
                Role = w.Role,
                ExtraMinutes = extra,
                EstimateMinutes = estimate,
                UserExtraMinutes = w.UserExtraMinutes,
                Kept = w.Kept,
                Decided = decided,
                Recording = w.Recording,
                EndUtc = w.EndUtc,
                GameTitle = w.GameTitle,
                GameStatus = w.GameStatus,
                Score = cfg.OverrunShowScores && _games.TryGetValue(w.GameProgramId, out var tr) && tr.Game is { } gg ? gg.Score : string.Empty
            });
        }

        return marks;
    }

    // A late game pushes the channel's shows back: the whole evening, or only the next show.
    private void AddShifted(GuideMarks marks, PluginConfiguration cfg, LiveTvProgram game, DateTime estimatedEnd)
    {
        var mode = ChannelMode(cfg, game.ChannelId);
        if (mode == PluginConfiguration.ChannelJoins)
        {
            return;
        }

        var listedEnd = EndOf(game);
        var after = ChannelPrograms(game.ChannelId, listedEnd, listedEnd + EveningLength).Where(p => p.StartDate >= listedEnd.AddMinutes(-1)).ToList();
        var anchor = after.FirstOrDefault(p => !IsSports(p))?.StartDate;
        if (anchor is null)
        {
            return;
        }

        var shift = (int)Math.Round((estimatedEnd - anchor.Value).TotalMinutes);
        if (shift <= 0)
        {
            return;
        }

        foreach (var p in after.Where(p => p.StartDate >= anchor.Value))
        {
            marks.Shifted.Add(new GuideShift { ProgramId = p.Id.ToString("N"), ShiftMinutes = shift, StartUtc = p.StartDate, EndUtc = EndOf(p) });
            if (mode == PluginConfiguration.ChannelNext)
            {
                break;
            }
        }
    }

    /// <summary>The guides' marks.</summary>
    public sealed class GuideMarks
    {
        /// <summary>"affected", "all" or "off".</summary>
        public string Marks { get; set; } = PluginConfiguration.GuideMarksAffected;

        public bool ShowScores { get; set; }

        public bool ShowEstimates { get; set; }

        /// <summary>Offer "Keep the normal end" / "Add 15 more minutes" (setting on, and the user may manage recordings).</summary>
        public bool Buttons { get; set; }

        /// <summary>Preview mode: nothing is really changed.</summary>
        public bool Preview { get; set; }

        public List<GuideGame> Games { get; } = new();

        public List<GuideRecording> Recordings { get; } = new();

        public List<GuideShift> Shifted { get; } = new();
    }

    public sealed class GuideGame
    {
        public string ProgramId { get; set; } = string.Empty;

        public string ChannelId { get; set; } = string.Empty;

        public DateTime ListedStartUtc { get; set; }

        public DateTime ListedEndUtc { get; set; }

        public DateTime? EstimatedEndUtc { get; set; }

        /// <summary>Expected to end more than 2 minutes after its listed end.</summary>
        public bool Late { get; set; }

        public bool Final { get; set; }

        /// <summary>"4th 2:10", "Halftime", "Final".</summary>
        public string Clock { get; set; } = string.Empty;

        /// <summary>Empty unless scores are shown.</summary>
        public string Score { get; set; } = string.Empty;
    }

    public sealed class GuideRecording
    {
        public string ProgramId { get; set; } = string.Empty;

        public string TimerId { get; set; } = string.Empty;

        public string ChannelId { get; set; } = string.Empty;

        /// <summary>"after", "game", "followup".</summary>
        public string Role { get; set; } = string.Empty;

        /// <summary>Extra minutes decided (0 before the decision).</summary>
        public int ExtraMinutes { get; set; }

        /// <summary>The estimate before the decision (0 when estimates aren't shown).</summary>
        public int EstimateMinutes { get; set; }

        public int UserExtraMinutes { get; set; }

        public bool Kept { get; set; }

        public bool Decided { get; set; }

        /// <summary>Already recording: the buttons can't change it any more.</summary>
        public bool Recording { get; set; }

        /// <summary>The programme's listed end.</summary>
        public DateTime EndUtc { get; set; }

        public string GameTitle { get; set; } = string.Empty;

        public string GameStatus { get; set; } = string.Empty;

        public string Score { get; set; } = string.Empty;
    }

    public sealed class GuideShift
    {
        public string ProgramId { get; set; } = string.Empty;

        public int ShiftMinutes { get; set; }

        /// <summary>Its listed times (the guide places the bar from them).</summary>
        public DateTime StartUtc { get; set; }

        public DateTime EndUtc { get; set; }
    }

    // ---- by hand ------------------------------------------------------------------------------

    /// <summary>"Keep the normal end" (keep = true) or "Follow the game again" (false).</summary>
    public async Task<string?> KeepAsync(string timerId, bool keep, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (t, error) = await ChangeableTimerAsync(timerId, ct).ConfigureAwait(false);
            if (t is null)
            {
                return error;
            }

            var done = _store.ChangeFor(t.Id, t.StartDate) ?? NewChange(t);
            if (keep)
            {
                t.PrePaddingSeconds = done.OriginalPrePadding;
                t.PostPaddingSeconds = done.OriginalPostPadding + (done.UserExtraMinutes * 60);
                await LiveTv.UpdateTimer(t, ct).ConfigureAwait(false);
                done.ExtraMinutes = 0;
                done.LaterStartMinutes = 0;
            }

            done.UserKept = keep;
            done.ChangedUtc = DateTime.UtcNow;
            _store.Save(done);
            Note($"{t.Name}: " + (keep ? "kept to its normal end by hand" : "follows the game again"));
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>"Add 15 more minutes".</summary>
    public async Task<string?> AddMinutesAsync(string timerId, int minutes, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (t, error) = await ChangeableTimerAsync(timerId, ct).ConfigureAwait(false);
            if (t is null)
            {
                return error;
            }

            var done = _store.ChangeFor(t.Id, t.StartDate) ?? NewChange(t);
            t.PostPaddingSeconds += minutes * 60;
            await LiveTv.UpdateTimer(t, ct).ConfigureAwait(false);
            done.UserExtraMinutes += minutes;
            done.ChangedUtc = DateTime.UtcNow;
            _store.Save(done);
            Note($"{t.Name}: +{minutes} min added by hand");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static OverrunStore.Change NewChange(TimerInfoDto t) => new()
    {
        TimerId = t.Id,
        StartUtc = t.StartDate,
        Title = t.Name ?? string.Empty,
        OriginalPrePadding = t.PrePaddingSeconds,
        OriginalPostPadding = t.PostPaddingSeconds,
        Reason = "added by hand"
    };

    private async Task<(TimerInfoDto? Timer, string Error)> ChangeableTimerAsync(string timerId, CancellationToken ct)
    {
        var t = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items.FirstOrDefault(x => x.Id == timerId);
        if (t is null || t.Status is not (RecordingStatus.New or RecordingStatus.InProgress))
        {
            return (null, "That recording isn't scheduled any more.");
        }

        if (t.Status == RecordingStatus.InProgress || _recordings.GetActiveRecordingPath(t.Id) is not null)
        {
            return (null, "It's already recording: Jellyfin can't change its end now.");
        }

        return (t, string.Empty);
    }

    // ---- telling ---------------------------------------------------------------------------

    private readonly ConcurrentDictionary<string, byte> _told = new();

    private async Task AlertAsync(PluginConfiguration cfg, string key, string title, string text, Watch w, CancellationToken ct)
    {
        if (!_told.TryAdd(key + "|" + cfg.OverrunMode, 0))
        {
            return; // said already
        }

        Note(title + ": " + text);
        if (!cfg.OverrunAlerts || cfg.OverrunMode != PluginConfiguration.ModeActive)
        {
            return;
        }

        await _center.RaiseAsync(
            new AlertReport
            {
                Kind = AlertKinds.Short,
                Key = key,
                Title = title,
                Text = text,
                Where = w.ChannelName,
                AiredUtc = w.StartUtc,
                ProgramId = w.ProgramId
            },
            ct).ConfigureAwait(false);
    }

    private void Note(string text)
    {
        _logger.LogInformation("Sports overruns: {Text}", text);
        _recent.Enqueue(DateTime.Now.ToString("ddd h:mm tt", CultureInfo.InvariantCulture) + "  " + text);
        while (_recent.Count > 100 && _recent.TryDequeue(out _))
        {
        }
    }

    internal static string Local(DateTime utc) => utc.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);
}
