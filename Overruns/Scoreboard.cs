using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Overruns;

/// <summary>The kinds of sport, each with its own clock and its own fixed extra time.</summary>
public static class SportKinds
{
    public const string Nfl = "nfl";
    public const string CollegeFootball = "college-football";
    public const string Basketball = "basketball";
    public const string Hockey = "hockey";
    public const string Baseball = "baseball";
    public const string Soccer = "soccer";
    public const string Other = "other";

    public static readonly string[] All = { Nfl, CollegeFootball, Basketball, Hockey, Baseball, Soccer, Other };
}

/// <summary>One game on ESPN's scoreboard.</summary>
public sealed class Game
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The scoreboard it came from ("football/nfl").</summary>
    public string League { get; set; } = string.Empty;

    /// <summary>One of <see cref="SportKinds"/>.</summary>
    public string Kind { get; set; } = SportKinds.Other;

    /// <summary>"Pittsburgh Steelers at Cincinnati Bengals".</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime StartUtc { get; set; }

    /// <summary>"pre", "in" or "post".</summary>
    public string State { get; set; } = "pre";

    /// <summary>"STATUS_HALFTIME", "STATUS_FINAL"…</summary>
    public string StatusName { get; set; } = string.Empty;

    /// <summary>"Q4 2:14", "Halftime", "Bot 7th", "Final".</summary>
    public string Detail { get; set; } = string.Empty;

    public int Period { get; set; }

    /// <summary>Game clock in seconds (time left, or time played for soccer).</summary>
    public double Clock { get; set; }

    /// <summary>The names each team goes by, for matching guide titles.</summary>
    public List<string[]> Teams { get; } = new();

    /// <summary>"PIT 24 – CIN 21" (away first) once the game has started; empty before.</summary>
    public string Score { get; set; } = string.Empty;

    public bool IsFinal => State == "post";

    public bool IsLive => State == "in";
}

/// <summary>
/// Reads game status from ESPN's public scoreboard (site.api.espn.com: no key, no account; the
/// same data espn.com's own score strip uses). Only league and date go out, nothing about the
/// server or its users. Each league/date is fetched at most once per <see cref="MaxAge"/>.
/// </summary>
public sealed class Scoreboard
{
    private const string Base = "https://site.api.espn.com/apis/site/v2/sports/";

    // league path, kind, extra query (college: all of Division I, not only the top 25)
    private static readonly (string Path, string Kind, string Query)[] Leagues =
    {
        ("football/nfl", SportKinds.Nfl, string.Empty),
        ("football/college-football", SportKinds.CollegeFootball, "&groups=80&limit=300"),
        ("basketball/nba", SportKinds.Basketball, string.Empty),
        ("basketball/wnba", SportKinds.Basketball, string.Empty),
        ("basketball/mens-college-basketball", SportKinds.Basketball, "&groups=50&limit=400"),
        ("basketball/womens-college-basketball", SportKinds.Basketball, "&groups=50&limit=400"),
        ("hockey/nhl", SportKinds.Hockey, string.Empty),
        ("baseball/mlb", SportKinds.Baseball, string.Empty),
        ("soccer/usa.1", SportKinds.Soccer, string.Empty),
        ("soccer/eng.1", SportKinds.Soccer, string.Empty),
        ("soccer/uefa.champions", SportKinds.Soccer, string.Empty),
        ("soccer/fifa.world", SportKinds.Soccer, string.Empty),
    };

    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<Scoreboard> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, List<Game> Games)> _cache = new();

    public Scoreboard(IHttpClientFactory http, ILogger<Scoreboard> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>How old a fetched scoreboard may be (set from "Check the score every").</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>When the scoreboard last answered, and the last failure, for the settings page.</summary>
    public DateTime? LastOkUtc { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>The leagues a guide entry could be from, by its words; all of them if it doesn't say.</summary>
    public static IEnumerable<(string Path, string Kind, string Query)> LeaguesFor(string text)
    {
        var t = " " + text.ToLowerInvariant() + " ";
        bool Has(params string[] words) => words.Any(w => t.Contains(w, StringComparison.Ordinal));
        var picked = Leagues.Where(l => l.Path switch
        {
            "football/nfl" => Has(" nfl", "football") && !Has("college", "ncaa"),
            "football/college-football" => Has("college football", "ncaa football", "football") && !Has(" nfl"),
            "basketball/nba" => Has(" nba") || (Has("basketball") && !Has("college", "ncaa", "wnba", "women")),
            "basketball/wnba" => Has("wnba"),
            "basketball/mens-college-basketball" => Has("basketball") && Has("college", "ncaa") && !Has("women"),
            "basketball/womens-college-basketball" => Has("basketball") && Has("women") && Has("college", "ncaa"),
            "hockey/nhl" => Has(" nhl", "hockey"),
            "baseball/mlb" => Has(" mlb", "baseball"),
            _ => Has("soccer", "premier league", " mls", "champions league", "world cup", " fc ", "united"),
        }).ToList();
        return picked.Count > 0 ? picked : Leagues.Where(l => l.Kind != SportKinds.Soccer);
    }

    /// <summary>
    /// The game a guide entry shows: both teams named in its title or episode title, starting near
    /// its start. Null when none matches (a studio show, a sport ESPN doesn't cover).
    /// </summary>
    public async Task<Game?> FindAsync(string title, string? episodeTitle, IEnumerable<string> genres, DateTime startUtc, CancellationToken ct)
    {
        var text = title + " " + episodeTitle;
        var all = new List<Game>();
        foreach (var league in LeaguesFor(text + " " + string.Join(' ', genres)))
        {
            // ESPN's dates are US Eastern days; a late game can be on the next UTC day.
            foreach (var day in new[] { startUtc.AddHours(-5).Date, startUtc.AddHours(-5).Date.AddDays(-1) }.Distinct())
            {
                all.AddRange(await GetAsync(league, day, ct).ConfigureAwait(false));
            }
        }

        return Match(text, startUtc, all);
    }

    /// <summary>The game again, fresh enough (same league/date fetch, refetched once stale).</summary>
    public async Task<Game?> RefreshAsync(Game game, CancellationToken ct)
    {
        var league = Leagues.FirstOrDefault(l => l.Path == game.League);
        if (league.Path is null)
        {
            return null;
        }

        var day = game.StartUtc.AddHours(-5).Date;
        return (await GetAsync(league, day, ct).ConfigureAwait(false)).FirstOrDefault(g => g.Id == game.Id);
    }

    /// <summary>Picks the game whose two teams are both named in <paramref name="text"/> (pure, for tests).</summary>
    public static Game? Match(string text, DateTime startUtc, IEnumerable<Game> games)
    {
        var words = Normalise(text);
        return games
            .Where(g => Math.Abs((g.StartUtc - startUtc).TotalMinutes) <= 150 && g.Teams.Count == 2
                && g.Teams.All(names => names.Any(n => n.Length >= 3 && words.Contains(" " + Normalise(n).Trim() + " ", StringComparison.Ordinal))))
            .OrderBy(g => Math.Abs((g.StartUtc - startUtc).TotalMinutes))
            .FirstOrDefault();
    }

    private static string Normalise(string s)
    {
        var lower = s.ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal).Replace("st.", "st", StringComparison.Ordinal);
        return " " + Regex.Replace(lower, "[^a-z0-9]+", " ").Trim() + " ";
    }

    private async Task<List<Game>> GetAsync((string Path, string Kind, string Query) league, DateTime easternDay, CancellationToken ct)
    {
        var key = league.Path + "|" + easternDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < MaxAge)
        {
            return hit.Games;
        }

        var url = Base + league.Path + "/scoreboard?dates=" + easternDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + league.Query;
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var games = Parse(doc.RootElement, league.Path, league.Kind);
            _cache[key] = (DateTime.UtcNow, games);
            LastOkUtc = DateTime.UtcNow;
            return games;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            LastError = DateTime.UtcNow.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture) + ": " + league.Path + ": " + ex.Message;
            _logger.LogDebug(ex, "Scoreboard {Url} failed", url);
            // Keep what we had and ask again in a minute.
            var kept = hit.Games ?? new List<Game>();
            _cache[key] = (DateTime.UtcNow - MaxAge + RetryAfter, kept);
            return kept;
        }
    }

    /// <summary>Reads one scoreboard response (pure, for tests).</summary>
    public static List<Game> Parse(JsonElement root, string league, string kind)
    {
        var games = new List<Game>();
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return games;
        }

        foreach (var e in events.EnumerateArray())
        {
            var g = new Game { League = league, Kind = kind, Id = Str(e, "id"), Name = Str(e, "name") };
            if (DateTime.TryParse(Str(e, "date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start))
            {
                g.StartUtc = start;
            }

            if (e.TryGetProperty("status", out var status))
            {
                g.Period = status.TryGetProperty("period", out var p) && p.TryGetInt32(out var pi) ? pi : 0;
                g.Clock = status.TryGetProperty("clock", out var c) && c.TryGetDouble(out var cd) ? cd : 0;
                if (status.TryGetProperty("type", out var type))
                {
                    g.State = Str(type, "state");
                    g.StatusName = Str(type, "name");
                    g.Detail = Str(type, "shortDetail");
                }
            }

            if (e.TryGetProperty("competitions", out var comps) && comps.ValueKind == JsonValueKind.Array && comps.GetArrayLength() > 0
                && comps[0].TryGetProperty("competitors", out var competitors))
            {
                var sides = new List<(bool Home, string Text)>();
                foreach (var team in competitors.EnumerateArray())
                {
                    if (team.TryGetProperty("team", out var t))
                    {
                        var abbr = Str(t, "abbreviation");
                        sides.Add((Str(team, "homeAway") == "home", (string.IsNullOrEmpty(abbr) ? Str(t, "shortDisplayName") : abbr) + " " + Str(team, "score")));
                        g.Teams.Add(new[] { Str(t, "displayName"), Str(t, "shortDisplayName"), Str(t, "name"), Str(t, "location") + " " + Str(t, "name") }
                            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray());
                    }
                }

                if (g.State != "pre" && sides.Count == 2)
                {
                    g.Score = string.Join(" – ", sides.OrderBy(x => x.Home).Select(x => x.Text.Trim()));
                }
            }

            games.Add(g);
        }

        return games;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
