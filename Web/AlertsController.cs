using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Alerts;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSchedule.Web;

/// <summary>
/// Problems for the TV client (anyone signed in: the household's recordings) and the Alerts page:
/// the list, dismissing one, and a test message (admin).
/// </summary>
[ApiController]
[Route("JellyDvrSchedule/Alerts")]
[Authorize]
public sealed class AlertsController : ControllerBase
{
    private readonly AlertCenter _center;
    private readonly AlertSenders _senders;

    public AlertsController(AlertCenter center, AlertSenders senders)
    {
        _center = center;
        _senders = senders;
    }

    /// <summary>Open problems, and those closed in the last <paramref name="days"/> days; what the TV should show.</summary>
    [HttpGet]
    public IActionResult List([FromQuery] int days = 7)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(days, 0, 30));
        var all = _center.List();
        var tvKinds = AlertKinds.All.Where(k => AlertCenter.RouteFor(k, cfg).Tv).ToHashSet(StringComparer.Ordinal);
        return Ok(new
        {
            Open = all.Where(a => a.State == Alert.Open).Select(a => Dto(a, tvKinds)),
            Closed = all.Where(a => a.State != Alert.Open && (a.ClosedUtc ?? a.UpdatedUtc) >= cutoff).Select(a => Dto(a, tvKinds)),
            Tv = new { PopupOnOpen = cfg.TvPopupOnOpen, ProblemsTile = cfg.TvProblemsTile, PopupDuringPlayback = cfg.TvPopupDuringPlayback }
        });
    }

    /// <summary>Dismisses every open problem; answers how many.</summary>
    [HttpPost("DismissAll")]
    public IActionResult DismissAll() => Ok(new { Dismissed = _center.DismissAll() });

    [HttpPost("{id}/Dismiss")]
    public IActionResult Dismiss([FromRoute] string id) => _center.Dismiss(id) ? NoContent() : NotFound();

    /// <summary>A test problem on the TVs' list (and popup): sent nowhere else; dismiss it there.</summary>
    [HttpPost("TestTv")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> TestTv(CancellationToken ct)
    {
        var now = DateTime.Now;
        await _center.RaiseAsync(
            new AlertReport
            {
                Kind = AlertKinds.Tuner,
                Key = "test:" + now.Ticks,
                Title = "Test problem from the Alerts page",
                Text = "This is how a problem shows on the TV. Nothing is wrong: dismiss it.",
                Next = "Dismiss it on the TV, or leave it: it stays until dismissed.",
                Source = "Alerts page"
            },
            ct,
            send: false).ConfigureAwait(false);
        return Ok(new { Ok = true, Message = "Added. Open JDVR (or bring it back to the front) to see the popup." });
    }

    /// <summary>Sends a test with the page's (maybe unsaved) settings; says why if it failed.</summary>
    [HttpPost("Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Test([FromQuery] string to, [FromBody] PluginConfiguration settings, CancellationToken ct)
    {
        try
        {
            await _senders.TestAsync(to == "email" ? "email" : "phone", settings, ct).ConfigureAwait(false);
            return Ok(new { Ok = true, Message = "Sent. Check your " + (to == "email" ? "inbox." : "phone.") });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Ok(new { Ok = false, Message = ex.Message });
        }
    }

    private static object Dto(Alert a, System.Collections.Generic.HashSet<string> tvKinds) => new
    {
        a.Id,
        a.Kind,
        a.Title,
        a.Text,
        a.Next,
        a.Where,
        a.AiredUtc,
        a.ItemId,
        a.ProgramId,
        a.Source,
        a.State,
        a.CreatedUtc,
        a.ClosedUtc,
        a.ClosedText,
        a.SentTo,
        a.Repeats,
        // Whether the TV shows this kind (the page's TV column).
        OnTv = tvKinds.Contains(a.Kind)
    };
}
