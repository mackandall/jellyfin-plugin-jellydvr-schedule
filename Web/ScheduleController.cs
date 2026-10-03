using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Schedule;
using MediaBrowser.Common.Api;
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
    private readonly LiveViewers _viewers;

    public ScheduleController(ScheduleManager manager, OutcomeStore store, ILibraryManager libraryManager, LiveViewerGuard guard, LiveViewers viewers)
    {
        _guard = guard;
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
        ViewerActions = _guard.Recent.ToList()
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
    /// "Add to favourites" from the guide: an exact-title favourite for this show (any channel; shows
    /// or movies to match it; skips episodes already in the library). Its upcoming airings are
    /// scheduled right after (in the background; the guide shows them as they're created).
    /// </summary>
    [HttpPost("Favourites/FromProgram/{programId}")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public IActionResult AddFavouriteFromProgram([FromRoute] Guid programId)
    {
        if (_libraryManager.GetItemById(programId) is not LiveTvProgram program || Plugin.Instance is not { } plugin)
        {
            return NotFound();
        }

        if (FavouriteFor(program) is { } existing)
        {
            return Ok(new { existing.Id, existing.Title, Added = false, Message = "Already a favourite: " + existing.Title });
        }

        var rule = new Configuration.FavouriteRule
        {
            Title = program.Name,
            Match = Configuration.FavouriteRule.MatchTitle,
            Type = program.IsMovie ? Configuration.FavouriteRule.TypeMovies : Configuration.FavouriteRule.TypeSeries,
            SkipInLibrary = true,
            Enabled = true
        };
        var cfg = plugin.Configuration;
        cfg.Favourites = (cfg.Favourites ?? Array.Empty<Configuration.FavouriteRule>()).Append(rule).ToArray();
        plugin.SaveConfiguration();
        _ = Task.Run(() => _manager.RunAsync("favourite added", CancellationToken.None));
        return Ok(new { rule.Id, rule.Title, Added = true, Message = "Added to favourites: " + rule.Title + ". Upcoming airings are being scheduled." });
    }

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
