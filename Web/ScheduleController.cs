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

    private static object FavouriteDto(FavouriteHit h) => new { h.ProgramId, h.RuleId, h.RuleTitle, h.Title, h.Channel, h.Start, h.End, h.Status };

    /// <summary>What the scheduler did with one guide airing: skipped / moved / dropped, and whether it is pinned.</summary>
    [HttpGet("Program/{programId}")]
    [Authorize(Policy = Policies.LiveTvAccess)]
    public IActionResult Program([FromRoute] Guid programId)
    {
        var o = _store.GetOutcome(programId);
        return Ok(new
        {
            Outcome = o is null ? null : new { o.Kind, o.Detail, o.RecordedInstead },
            Pinned = _store.IsPinned(programId)
        });
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
