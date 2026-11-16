using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DvrSchedule.Overruns;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSchedule.Web;

/// <summary>
/// Sports overruns: what the watch sees (the settings page, admin) and the extra time given to
/// recordings (the TV client: anyone signed in).
/// </summary>
[ApiController]
[Route("JellyDvrSchedule/Overruns")]
[Authorize]
public sealed class OverrunsController : ControllerBase
{
    private readonly OverrunWatcher _watcher;
    private readonly OverrunStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationService _authorization;

    public OverrunsController(OverrunWatcher watcher, OverrunStore store, ILibraryManager libraryManager, IAuthorizationService authorization)
    {
        _authorization = authorization;
        _watcher = watcher;
        _store = store;
        _libraryManager = libraryManager;
    }

    /// <summary>The watched recordings, games, recent actions and the channels that show games.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Status() => Ok(new
    {
        Mode = Plugin.Instance?.Configuration.OverrunMode,
        LastCheckUtc = _watcher.LastCheckUtc,
        ScoreboardOkUtc = _watcher.Board.LastOkUtc,
        ScoreboardError = _watcher.Board.LastError,
        Watches = _watcher.Watches,
        Recent = _watcher.Recent,
        Chains = _store.Chains().OrderByDescending(c => c.CreatedUtc).Select(c => new { c.Title, c.State, c.Note, Parts = c.TimerIds.Count, c.CreatedUtc }),
        Channels = SportsChannels()
    });

    /// <summary>Check now instead of at the next tick.</summary>
    [HttpPost("Check")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Check(CancellationToken ct)
    {
        await _watcher.CheckAsync(ct).ConfigureAwait(false);
        return Status();
    }

    /// <summary>
    /// Recordings given extra time (or a follow-up) for a late game, by timer id: what JDVR shows as
    /// "+40 min · game ran late". Only real changes (active mode), not previews.
    /// </summary>
    [HttpGet("Changes")]
    public IActionResult Changes()
    {
        var since = DateTime.UtcNow.AddHours(-12);
        return Ok(_store.Changes()
            .Where(c => c.StartUtc > since && (c.ExtraMinutes > 0 || c.UserExtraMinutes > 0 || c.Reason == "follow-up"))
            .Select(c => new { c.TimerId, c.StartUtc, c.Title, ExtraMinutes = (c.UserKept ? 0 : c.ExtraMinutes) + c.UserExtraMinutes, c.LaterStartMinutes, c.Reason }));
    }

    /// <summary>The guides' marks for late games (jellyfin-web's guide and JDVR).</summary>
    [HttpGet("Guide")]
    [Authorize(Policy = Policies.LiveTvAccess)]
    public async Task<IActionResult> Guide()
    {
        var canChange = (await _authorization.AuthorizeAsync(User, Policies.LiveTvManagement).ConfigureAwait(false)).Succeeded;
        return Ok(_watcher.Guide(canChange));
    }

    /// <summary>"Keep the normal end": the watch leaves this recording alone.</summary>
    [HttpPost("Timer/{timerId}/Keep")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public Task<IActionResult> Keep([FromRoute] string timerId, CancellationToken ct) => Act(_watcher.KeepAsync(timerId, true, ct), ct);

    /// <summary>"Follow the game again" after "Keep the normal end".</summary>
    [HttpDelete("Timer/{timerId}/Keep")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public Task<IActionResult> Unkeep([FromRoute] string timerId, CancellationToken ct) => Act(_watcher.KeepAsync(timerId, false, ct), ct);

    /// <summary>"Add 15 more minutes".</summary>
    [HttpPost("Timer/{timerId}/More")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public Task<IActionResult> More([FromRoute] string timerId, CancellationToken ct) => Act(_watcher.AddMinutesAsync(timerId, 15, ct), ct);

    private async Task<IActionResult> Act(Task<string?> action, CancellationToken ct)
    {
        var error = await action.ConfigureAwait(false);
        if (error is not null)
        {
            return Conflict(new { Message = error });
        }

        await _watcher.CheckAsync(ct).ConfigureAwait(false);
        return Ok(_watcher.Guide(true));
    }

    // Channels with a game in the guide from yesterday to a week ahead.
    private object[] SportsChannels()
    {
        var now = DateTime.UtcNow;
        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.TvProgram },
                MinEndDate = now.AddDays(-1),
                MaxStartDate = now.AddDays(7)
            })
            .OfType<LiveTvProgram>()
            .Where(OverrunWatcher.IsGame)
            .GroupBy(p => p.ChannelId)
            .Select(g => (Channel: _libraryManager.GetItemById(g.Key) as LiveTvChannel, Games: g.Count()))
            .Where(x => x.Channel is not null)
            .OrderBy(x => x.Channel!.Number?.Length ?? 0).ThenBy(x => x.Channel!.Number).ThenBy(x => x.Channel!.Name)
            .Select(x => (object)new { Id = x.Channel!.Id.ToString("N"), x.Channel.Name, x.Channel.Number, x.Games })
            .ToArray();
    }
}
