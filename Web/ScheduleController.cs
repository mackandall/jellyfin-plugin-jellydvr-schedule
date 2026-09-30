using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Schedule;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSchedule.Web;

/// <summary>Status and "check now" for the settings page. Admin only: this cancels and moves recordings.</summary>
[ApiController]
[Route("JellyDvrSchedule")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ScheduleController : ControllerBase
{
    private readonly ScheduleManager _manager;

    public ScheduleController(ScheduleManager manager)
    {
        _manager = manager;
    }

    [HttpGet("Status")]
    public IActionResult Status() => Ok(new { Plan = Dto(_manager.LastPlan), Recent = _manager.RecentActions });

    [HttpPost("Run")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        var plan = await _manager.RunAsync("manual", cancellationToken).ConfigureAwait(false);
        return Ok(Dto(plan));
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
            MoveTo = d.MoveTo is null ? null : new { d.MoveTo.ChannelName, d.MoveTo.Start },
            d.SlotStart,
            d.SlotEnd,
            Competing = d.Competing.Select(c => new { c.Title, c.ChannelName, Start = c.Start + c.PadBefore, c.Rank, c.Locked })
        })
    };
}
