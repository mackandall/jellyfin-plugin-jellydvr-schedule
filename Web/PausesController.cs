using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Schedule;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSchedule.Web;

/// <summary>
/// Pausing series recordings, for everyone allowed to manage recordings: while paused, the episodes a
/// series recording schedules are cancelled (all of them, or only those before a date); resuming
/// schedules them again. The series recording itself and its settings stay as they are.
/// </summary>
[ApiController]
[Route("JellyDvrSchedule/Pauses")]
public sealed class PausesController : ControllerBase
{
    private readonly ScheduleManager _manager;

    public PausesController(ScheduleManager manager)
    {
        _manager = manager;
    }

    public sealed class PauseInput
    {
        /// <summary>ISO time (UTC or with offset) to resume at; empty or missing: until resumed.</summary>
        public string? Until { get; set; }

        /// <summary>"next": skip only the next episode; "premiere": until the next season starts. Until is then ignored.</summary>
        public string? Mode { get; set; }
    }

    /// <summary>Pauses a series recording, or changes until when. Body (optional): {"Until": "2026-11-09T05:00:00Z"}.</summary>
    [HttpPost("{seriesTimerId}")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> Pause([FromRoute] string seriesTimerId, CancellationToken cancellationToken)
    {
        PauseInput? input = null;
        try
        {
            input = await System.Text.Json.JsonSerializer.DeserializeAsync<PauseInput>(Request.Body, WebInput.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            // no or empty body: until resumed
        }

        DateTime? until = null;
        var mode = input?.Mode is ScheduleManager.PauseModeNext or ScheduleManager.PauseModePremiere ? input.Mode : null;
        if (mode is null && !WebInput.TryFuture(input?.Until, out until))
        {
            return BadRequest("Until must be a future date and time.");
        }

        var message = await _manager.PauseSeriesAsync(seriesTimerId, until, cancellationToken, mode).ConfigureAwait(false);
        return message is null ? NotFound("No such series recording.") : Ok(new { Paused = true, Until = until, Message = message });
    }

    /// <summary>Resumes a paused series recording: the episodes the pause cancelled are scheduled again.</summary>
    [HttpDelete("{seriesTimerId}")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public IActionResult Resume([FromRoute] string seriesTimerId)
    {
        var message = _manager.ResumeSeries(seriesTimerId);
        return message is null ? NotFound("That series recording isn't paused.") : Ok(new { Paused = false, Message = message });
    }
}
