using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Schedule;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSchedule.Web;

/// <summary>
/// Recording rules: series recordings and Recording Favourites in one list (web Live TV › Series,
/// JDVR's Series screen), the priority order, and pausing everything at once.
/// </summary>
[ApiController]
[Route("JellyDvrSchedule/Rules")]
public sealed class RulesController : ControllerBase
{

    private readonly RuleBoard _board;
    private readonly ScheduleManager _manager;
    private readonly IUserManager _users;
    private readonly IAuthorizationService _authorization;

    public RulesController(RuleBoard board, ScheduleManager manager, IUserManager users, IAuthorizationService authorization)
    {
        _board = board;
        _manager = manager;
        _users = users;
        _authorization = authorization;
    }

    /// <summary>Every rule, highest priority first; counts of unwatched recordings are the asking user's.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.LiveTvAccess)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var user = Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) && id != Guid.Empty ? _users.GetUserById(id) : null;
        var rules = await _board.BuildAsync(user, cancellationToken).ConfigureAwait(false);
        var canManage = (await _authorization.AuthorizeAsync(User, Policies.LiveTvManagement).ConfigureAwait(false)).Succeeded;
        return Ok(new { CanManage = canManage, Rules = rules });
    }

    /// <summary>The priority order: a JSON array of rule ids, highest first.</summary>
    [HttpPost("Order")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> Order()
    {
        string[]? keys;
        try
        {
            keys = await JsonSerializer.DeserializeAsync<string[]>(Request.Body, WebInput.Json).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            keys = null;
        }

        if (keys is null || keys.Length == 0)
        {
            return BadRequest("Send the rule ids in order.");
        }

        _manager.SetPriority(keys);
        return Ok(new { Message = "Priorities saved." });
    }

    public sealed class UntilInput
    {
        public string? Until { get; set; }
    }

    /// <summary>Pauses every series recording and (when Until is given) switches off every favourite.</summary>
    [HttpPost("PauseAll")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> PauseAll(CancellationToken cancellationToken)
    {
        UntilInput? input = null;
        try
        {
            input = await JsonSerializer.DeserializeAsync<UntilInput>(Request.Body, WebInput.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
        }

        if (!WebInput.TryFuture(input?.Until, out var until))
        {
            return BadRequest("Until must be a future date and time.");
        }

        return Ok(new { Message = await _manager.PauseAllAsync(until, cancellationToken).ConfigureAwait(false) });
    }

    /// <summary>Resumes every paused series recording and switches on favourites that were off for a while.</summary>
    [HttpPost("ResumeAll")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public IActionResult ResumeAll() => Ok(new { Message = _manager.ResumeAll() });
}
