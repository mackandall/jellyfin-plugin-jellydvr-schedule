using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using Jellyfin.Plugin.DvrSchedule.Schedule;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DvrSchedule.Web;

/// <summary>
/// Favourites for everyone allowed to manage recordings (not only admins): the list under Live TV in
/// the clients, with each favourite's next recordings, and add / edit / remove. The admin settings
/// page keeps the priority order and the advanced preview.
/// </summary>
[ApiController]
[Route("JellyDvrSchedule/Favourites")]
public sealed class FavouritesController : ControllerBase
{
    private const int NextShown = 5;

    private readonly ScheduleManager _manager;
    private readonly IAuthorizationService _authorization;

    public FavouritesController(ScheduleManager manager, IAuthorizationService authorization)
    {
        _manager = manager;
        _authorization = authorization;
    }

    /// <summary>Who may change favourites is the same as who may schedule recordings.</summary>
    private async Task<bool> CanManageAsync() =>
        (await _authorization.AuthorizeAsync(User, Policies.LiveTvManagement).ConfigureAwait(false)).Succeeded;

    /// <summary>The favourites, each with its next scheduled recordings (from the last favourites run).</summary>
    [HttpGet]
    [Authorize(Policy = Policies.LiveTvAccess)]
    public async Task<IActionResult> List()
    {
        var rules = Plugin.Instance?.Configuration.Favourites ?? Array.Empty<FavouriteRule>();
        var hits = _manager.LastFavourites;
        var now = DateTime.UtcNow;
        return Ok(new
        {
            CanManage = await CanManageAsync().ConfigureAwait(false),
            Favourites = rules.Select(r =>
            {
                var upcoming = hits
                    .Where(h => h.RuleId == r.Id && h.End > now && (h.Status == FavouriteHit.Scheduled || h.Status == FavouriteHit.WillRecord))
                    .OrderBy(h => h.Start)
                    .ToList();
                return new
                {
                    r.Id,
                    r.Title,
                    r.Match,
                    r.Channels,
                    r.NewOnly,
                    r.Type,
                    r.SkipInLibrary,
                    r.Enabled,
                    Upcoming = upcoming.Count,
                    Next = upcoming.Take(NextShown).Select(h => new { h.ProgramId, h.Title, h.Channel, h.Start })
                };
            })
        });
    }

    public sealed class FavouriteInput
    {
        public string? Title { get; set; }

        public string? Match { get; set; }

        public string? Channels { get; set; }

        public bool? NewOnly { get; set; }

        public string? Type { get; set; }

        public bool? SkipInLibrary { get; set; }

        public bool? Enabled { get; set; }
    }

    /// <summary>Adds a favourite (title required); its upcoming airings get scheduled right after.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public IActionResult Add([FromBody] FavouriteInput input)
    {
        if (Plugin.Instance is not { } plugin)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(input.Title))
        {
            return BadRequest("A title or keyword is needed.");
        }

        var rule = Apply(new FavouriteRule(), input);
        var cfg = plugin.Configuration;
        cfg.Favourites = (cfg.Favourites ?? Array.Empty<FavouriteRule>()).Append(rule).ToArray();
        plugin.SaveConfiguration();
        RunSoon("favourite added");
        return Ok(new { rule.Id, Message = "Added to favourites: " + rule.Title + ". Upcoming airings are being scheduled." });
    }

    /// <summary>Changes a favourite (only the fields sent). Switched off, it keeps what's scheduled.</summary>
    [HttpPost("{ruleId}")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public IActionResult Update([FromRoute] string ruleId, [FromBody] FavouriteInput input)
    {
        if (Plugin.Instance is not { } plugin)
        {
            return NotFound();
        }

        var rule = (plugin.Configuration.Favourites ?? Array.Empty<FavouriteRule>()).FirstOrDefault(r => r.Id == ruleId);
        if (rule is null)
        {
            return NotFound();
        }

        if (input.Title is not null && string.IsNullOrWhiteSpace(input.Title))
        {
            return BadRequest("A title or keyword is needed.");
        }

        Apply(rule, input);
        plugin.SaveConfiguration();
        RunSoon("favourite changed");
        return Ok(new { rule.Id, Message = "Saved: " + rule.Title });
    }

    private static FavouriteRule Apply(FavouriteRule rule, FavouriteInput input)
    {
        if (input.Title is not null)
        {
            rule.Title = input.Title.Trim();
        }

        if (input.Match is FavouriteRule.MatchTitle or FavouriteRule.MatchKeyword)
        {
            rule.Match = input.Match;
        }

        if (input.Channels is not null)
        {
            rule.Channels = input.Channels.Trim();
        }

        if (input.Type is FavouriteRule.TypeAny or FavouriteRule.TypeSeries or FavouriteRule.TypeMovies)
        {
            rule.Type = input.Type;
        }

        rule.NewOnly = input.NewOnly ?? rule.NewOnly;
        rule.SkipInLibrary = input.SkipInLibrary ?? rule.SkipInLibrary;
        rule.Enabled = input.Enabled ?? rule.Enabled;
        return rule;
    }

    // Planning takes a few seconds (Jellyfin's timer list is slow): answer now, schedule after.
    private void RunSoon(string reason) => _ = Task.Run(() => _manager.RunAsync(reason, CancellationToken.None));
}
