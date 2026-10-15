using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Schedule;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSchedule.Web;

/// <summary>
/// Status and "check now" for the settings page (admin only), plus the guide's per-airing state and
/// "Record this instead" (anyone allowed to manage recordings).
/// </summary>
[ApiController]
[Route("JellyDvrSchedule")]
public sealed class ScheduleController : ControllerBase
{
    private readonly ScheduleManager _manager;
    private readonly OutcomeStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly LiveViewerGuard _guard;
    private readonly RecordingStartWatchdog _watchdog;
    private readonly RecordingStallWatchdog _stalls;
    private readonly LiveViewers _viewers;

    public ScheduleController(ScheduleManager manager, OutcomeStore store, ILibraryManager libraryManager, LiveViewerGuard guard, LiveViewers viewers, RecordingStartWatchdog watchdog, RecordingStallWatchdog stalls)
    {
        _guard = guard;
        _watchdog = watchdog;
        _stalls = stalls;
        _viewers = viewers;
        _manager = manager;
        _store = store;
        _libraryManager = libraryManager;
    }

    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Status() => Ok(new
    {
        Plan = Dto(_manager.LastPlan),
        Recent = _manager.RecentActions,
        Outcomes = _store.Outcomes.OrderBy(o => o.Start),
        Pins = _store.Pins,
        Favourites = _manager.LastFavourites.Select(FavouriteDto),
        Viewers = _viewers.Current().Select(v => v.Describe() + (string.IsNullOrEmpty(v.User) ? string.Empty : " (" + v.User + ")")),
        ViewerActions = _guard.Recent.ToList(),
        NotStarted = _watchdog.Recent.ToList(),
        Stalled = _stalls.RecentNotes()
    });

    [HttpPost("Run")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        var plan = await _manager.RunAsync("manual", cancellationToken).ConfigureAwait(false);
        return Ok(Dto(plan));
    }

    /// <summary>
    /// What favourite rules (the ones posted, e.g. while editing; saved ones if none) would match now:
    /// upcoming airings and what would happen to each. Schedules nothing.
    /// </summary>
    [HttpPost("Favourites/Preview")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> PreviewFavourites([FromBody] Configuration.FavouriteRule[]? rules, CancellationToken cancellationToken)
    {
        var use = rules is { Length: > 0 } ? rules : Plugin.Instance?.Configuration.Favourites ?? Array.Empty<Configuration.FavouriteRule>();
        foreach (var r in use)
        {
            r.Enabled = true; // previewing a rule shows its matches even while it's switched off
        }

        var hits = await _manager.PreviewFavouritesAsync(use, cancellationToken).ConfigureAwait(false);
        return Ok(hits.Select(FavouriteDto));
    }

    /// <summary>
    /// Favourites matching each series recording (title, its channel unless it records on any channel,
    /// new-only and skip-in-library as set), for series not already covered by a favourite of the same
    /// title. Saves nothing: the settings page adds them to the list for review.
    /// </summary>
    [HttpGet("Favourites/FromSeries")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> FavouritesFromSeries(CancellationToken cancellationToken)
    {
        var liveTv = HttpContext.RequestServices.GetService(typeof(ILiveTvManager)) as ILiveTvManager;
        if (liveTv is null)
        {
            return Ok(Array.Empty<object>());
        }

        var existing = (Plugin.Instance?.Configuration.Favourites ?? Array.Empty<Configuration.FavouriteRule>())
            .Select(f => FavouriteMatcher.Normalize(f.Title))
            .ToHashSet(StringComparer.Ordinal);
        var series = (await liveTv.GetSeriesTimers(new MediaBrowser.Model.LiveTv.SeriesTimerQuery(), cancellationToken).ConfigureAwait(false)).Items;
        var rules = series
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !existing.Contains(FavouriteMatcher.Normalize(s.Name)))
            .GroupBy(s => FavouriteMatcher.Normalize(s.Name))
            .Select(g =>
            {
                var anyChannel = g.Any(s => s.RecordAnyChannel);
                var numbers = anyChannel
                    ? Array.Empty<string>()
                    : g.Select(s => (_libraryManager.GetItemById(s.ChannelId) as LiveTvChannel)?.Number).OfType<string>().Distinct().ToArray();
                return new Configuration.FavouriteRule
                {
                    Title = g.First().Name,
                    Match = Configuration.FavouriteRule.MatchTitle,
                    Channels = string.Join(", ", numbers),
                    NewOnly = g.All(s => s.RecordNewOnly),
                    Type = Configuration.FavouriteRule.TypeSeries,
                    SkipInLibrary = g.Any(s => s.SkipEpisodesInLibrary),
                    Enabled = true
                };
            })
            .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(rules);
    }

    private static object FavouriteDto(FavouriteHit h) => new { h.ProgramId, h.RuleId, h.RuleTitle, h.Title, h.Channel, h.Start, h.End, h.Status };

    /// <summary>What the scheduler did with one guide airing: skipped / moved / dropped, and whether it is pinned.</summary>
    [HttpGet("Program/{programId}")]
    [Authorize(Policy = Policies.LiveTvAccess)]
    public IActionResult Program([FromRoute] Guid programId)
    {
        var o = _store.GetOutcome(programId);
        var rule = _libraryManager.GetItemById(programId) is LiveTvProgram program ? FavouriteFor(program) : null;
        return Ok(new
        {
            Outcome = o is null ? null : new { o.Kind, o.Detail, o.RecordedInstead },
            Pinned = _store.IsPinned(programId),
            // The favourite that covers this show, if any (title, type and channels; "new only" ignored,
            // so a repeat still shows its show is a favourite).
            Favourite = rule is null ? null : new { rule.Id, rule.Title, rule.Match }
        });
    }

    private Configuration.FavouriteRule? FavouriteFor(LiveTvProgram program)
    {
        var channel = _libraryManager.GetItemById(program.ChannelId) as LiveTvChannel;
        return (Plugin.Instance?.Configuration.Favourites ?? Array.Empty<Configuration.FavouriteRule>())
            .FirstOrDefault(r => r.Enabled && FavouriteMatcher.Matches(r, program.Name, program.IsMovie, false, channel?.Number, channel?.Name));
    }

    /// <summary>
    /// The show (or movie) an item stands for, as a favourite would name it: a guide programme's
    /// title, a recorded episode's series, a series, a movie. The guide may put a year in brackets
    /// after the name; favourites match without it, so it's left off.
    /// </summary>
    private (string Title, bool IsMovie)? ShowOf(BaseItem? item)
    {
        string? title = item switch
        {
            LiveTvProgram p => p.Name,
            MediaBrowser.Controller.Entities.TV.Episode e => e.SeriesName ?? e.Series?.Name,
            MediaBrowser.Controller.Entities.TV.Season s => s.SeriesName ?? s.Series?.Name,
            MediaBrowser.Controller.Entities.TV.Series s => s.Name,
            MediaBrowser.Controller.Entities.Movies.Movie m => m.Name,
            Video v => v.Name,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s*\(\d{4}\)\s*$", string.Empty).Trim();
        var isMovie = item is MediaBrowser.Controller.Entities.Movies.Movie || (item is LiveTvProgram lp && lp.IsMovie);
        return (title, isMovie);
    }

    // The favourite covering a show: title and type (channels and "new only" don't matter here).
    private static Configuration.FavouriteRule? FavouriteForShow(string title, bool isMovie) =>
        (Plugin.Instance?.Configuration.Favourites ?? Array.Empty<Configuration.FavouriteRule>())
            .FirstOrDefault(r => FavouriteMatcher.Matches(
                new Configuration.FavouriteRule { Title = r.Title, Match = r.Match, Type = r.Type },
                title,
                isMovie,
                false,
                null,
                null) && r.Enabled);

    /// <summary>Which show an item stands for and the favourite covering it, for "Add to Recording Favourites" menus.</summary>
    [HttpGet("Favourites/ForItem/{itemId}")]
    [Authorize(Policy = Policies.LiveTvAccess)]
    public IActionResult FavouriteForItem([FromRoute] Guid itemId)
    {
        if (ShowOf(_libraryManager.GetItemById(itemId)) is not { } show)
        {
            return NotFound();
        }

        var rule = FavouriteForShow(show.Title, show.IsMovie);
        return Ok(new { show.Title, show.IsMovie, Favourite = rule is null ? null : new { rule.Id, rule.Title, rule.Match } });
    }

    /// <summary>
    /// "Add to Recording Favourites" from a guide programme, a recording, a series or a movie: an
    /// exact-title favourite for the show (any channel; shows or movies to match; skips episodes
    /// already in the library). Its upcoming airings are scheduled right after, in the background.
    /// </summary>
    [HttpPost("Favourites/FromItem/{itemId}")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public IActionResult AddFavouriteFromItem([FromRoute] Guid itemId)
    {
        if (ShowOf(_libraryManager.GetItemById(itemId)) is not { } show || Plugin.Instance is not { } plugin)
        {
            return NotFound();
        }

        if (FavouriteForShow(show.Title, show.IsMovie) is { } existing)
        {
            return Ok(new { existing.Id, existing.Title, Added = false, Message = "Already a recording favourite: " + existing.Title });
        }

        var rule = new Configuration.FavouriteRule
        {
            Title = show.Title,
            Match = Configuration.FavouriteRule.MatchTitle,
            Type = show.IsMovie ? Configuration.FavouriteRule.TypeMovies : Configuration.FavouriteRule.TypeSeries,
            SkipInLibrary = true,
            Enabled = true
        };
        var cfg = plugin.Configuration;
        cfg.Favourites = (cfg.Favourites ?? Array.Empty<Configuration.FavouriteRule>()).Append(rule).ToArray();
        plugin.SaveConfiguration();
        _ = Task.Run(() => _manager.RunAsync("favourite added", CancellationToken.None));
        return Ok(new { rule.Id, rule.Title, Added = true, Message = "Added to recording favourites: " + rule.Title + ". Upcoming airings are being scheduled." });
    }

    /// <summary>Kept for older clients: same as <see cref="AddFavouriteFromItem"/>.</summary>
    [HttpPost("Favourites/FromProgram/{programId}")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public IActionResult AddFavouriteFromProgram([FromRoute] Guid programId) => AddFavouriteFromItem(programId);

    /// <summary>
    /// Removes a favourite and cancels the recordings it scheduled that haven't started (like cancelling
    /// a series recording). Recordings in progress and finished ones stay.
    /// </summary>
    [HttpDelete("Favourites/{ruleId}")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> RemoveFavourite([FromRoute] string ruleId, CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not { } plugin)
        {
            return NotFound();
        }

        var cfg = plugin.Configuration;
        var rule = (cfg.Favourites ?? Array.Empty<Configuration.FavouriteRule>()).FirstOrDefault(r => r.Id == ruleId);
        if (rule is null)
        {
            return NotFound();
        }

        cfg.Favourites = cfg.Favourites.Where(r => r.Id != ruleId).ToArray();
        cfg.SeriesPriority = (cfg.SeriesPriority ?? Array.Empty<string>()).Where(k => k != ScheduleManager.FavouriteKey(ruleId)).ToArray();
        plugin.SaveConfiguration();
        var cancelled = await _manager.CancelFavouriteRecordingsAsync(ruleId, cancellationToken).ConfigureAwait(false);
        return Ok(new { Removed = true, Cancelled = cancelled, Message = "Removed from favourites: " + rule.Title + (cancelled > 0 ? " (" + cancelled + " upcoming recording(s) cancelled)" : string.Empty) });
    }

    /// <summary>
    /// Record this airing whatever else has to give way: it outranks every other recording (except
    /// ones already recording), is never moved to another airing, and the plan runs at once.
    /// </summary>
    [HttpPost("RecordInstead/{programId}")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> RecordInstead([FromRoute] Guid programId, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(programId) is not LiveTvProgram program || !program.EndDate.HasValue)
        {
            return NotFound();
        }

        if (program.EndDate.Value < DateTime.UtcNow)
        {
            return BadRequest("This programme has already ended.");
        }

        var outcome = _store.GetOutcome(programId);
        _store.AddPin(new OutcomeStore.Pin
        {
            ProgramId = programId,
            Title = program.Name,
            End = program.EndDate.Value,
            CreatedUtc = DateTime.UtcNow
        });

        var plan = await _manager.RecordInsteadAsync(programId, outcome, cancellationToken).ConfigureAwait(false);
        var givingWay = plan.Decisions
            .Where(d => d.Kind is DecisionKind.Move or DecisionKind.Skip && d.Recording.ProgramId != programId)
            .Select(d => d.Kind == DecisionKind.Move
                ? d.Recording.Title + " moves to " + d.MoveTo!.ChannelName + " " + d.MoveTo.Start.ToLocalTime().ToString("ddd h:mm tt", System.Globalization.CultureInfo.InvariantCulture)
                : d.Recording.Title + " is skipped")
            .ToList();
        var lost = plan.Decisions.Any(d => d.Kind == DecisionKind.Skip && d.Recording.ProgramId == programId);
        return Ok(new
        {
            Recording = !lost,
            Message = lost
                ? "Can't record it: every tuner is busy with recordings already in progress."
                : givingWay.Count == 0 ? "Scheduled." : "Scheduled. " + string.Join("; ", givingWay) + ".",
            GivingWay = givingWay
        });
    }

    /// <summary>Takes back "Record this instead": the airing competes by its normal priority again.</summary>
    [HttpDelete("RecordInstead/{programId}")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> Unpin([FromRoute] Guid programId, CancellationToken cancellationToken)
    {
        var removed = _store.RemovePin(programId);
        if (removed)
        {
            await _manager.RunAsync("record-instead removed", cancellationToken).ConfigureAwait(false);
        }

        return Ok(new { Removed = removed });
    }

    private static object? Dto(ScheduleManager.SchedulePlan? p) => p is null ? null : new
    {
        p.CreatedUtc,
        p.Reason,
        p.Mode,
        p.Capacity,
        p.Scheduled,
        p.Warning,
        p.Series,
        Decisions = p.Decisions.Select(d => new
        {
            Kind = d.Kind.ToString(),
            d.Recording.Title,
            d.Recording.ChannelName,
            Start = d.Recording.Start + d.Recording.PadBefore,
            d.Recording.Rank,
            d.Recording.ProgramId,
            d.Recording.Pinned,
            MoveTo = d.MoveTo is null ? null : new { d.MoveTo.ChannelName, d.MoveTo.Start },
            d.SlotStart,
            d.SlotEnd,
            Competing = d.Competing.Select(c => new { c.Title, c.ChannelName, Start = c.Start + c.PadBefore, c.Rank, c.Locked }),
            Kept = d.KeptCopy is { } k ? new { k.ChannelName, Start = k.Start + k.PadBefore, k.IsRepeat } : null,
            d.Recording.IsRepeat
        })
    };
}
