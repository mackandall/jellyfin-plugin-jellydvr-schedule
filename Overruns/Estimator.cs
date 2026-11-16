using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DvrSchedule.Overruns;

/// <summary>
/// How much air time a game still needs, from its period and clock. Rough on purpose: a game
/// minute costs about 3 minutes of air in football (stoppages, ads), less in hockey; the safety
/// margin covers the rest. Pure, so it can be tested without a server.
/// </summary>
public static class Estimator
{
    /// <summary>
    /// Minutes of air time left, or null when the clock can't tell (the game hasn't started, or
    /// the scoreboard gives nothing to go on): the sport's fixed extra time is used then.
    /// </summary>
    public static double? MinutesLeft(Game g)
    {
        if (g.IsFinal)
        {
            return 0;
        }

        if (!g.IsLive)
        {
            return null;
        }

        var half = g.StatusName.Contains("HALFTIME", StringComparison.OrdinalIgnoreCase)
            || g.Detail.Contains("Half", StringComparison.OrdinalIgnoreCase) && !g.Detail.Contains("1st", StringComparison.Ordinal) && !g.Detail.Contains("2nd", StringComparison.Ordinal);
        var endOfPeriod = g.StatusName.Contains("END_PERIOD", StringComparison.OrdinalIgnoreCase);
        var clockMin = Math.Max(0, g.Clock) / 60.0;

        switch (g.Kind)
        {
            case SportKinds.Nfl:
            case SportKinds.CollegeFootball:
            {
                var perMinute = g.Kind == SportKinds.Nfl ? 3.0 : 3.5;
                if (half)
                {
                    return 15 + (30 * perMinute);
                }

                if (g.Period <= 4)
                {
                    var left = ((4 - g.Period) * 15) + (endOfPeriod ? 0 : clockMin);
                    return (left * perMinute) + (g.Period <= 2 ? 15 : 0) + (g.Period == 4 && left < 5 ? 8 : 0);
                }

                // Overtime: NFL has a 10-minute period; college plays possessions with no clock.
                return g.Kind == SportKinds.Nfl ? (clockMin * perMinute) + 5 : 20;
            }

            case SportKinds.Basketball:
            {
                // Men's college plays two 20-minute halves; NBA four 12s; WNBA and women's college four 10s.
                var (periods, length) = g.League.Contains("mens-college", StringComparison.Ordinal) && !g.League.Contains("womens", StringComparison.Ordinal)
                    ? (2, 20)
                    : g.League.Contains("nba", StringComparison.Ordinal) && !g.League.Contains("wnba", StringComparison.Ordinal) ? (4, 12) : (4, 10);
                const double perMinute = 2.8;
                if (half)
                {
                    return 15 + (periods * length / 2.0 * perMinute);
                }

                if (g.Period <= periods)
                {
                    var left = ((periods - g.Period) * length) + (endOfPeriod ? 0 : clockMin);
                    var halftimeLeft = g.Period <= periods / 2 ? 15 : 0;
                    return (left * perMinute) + halftimeLeft + (left < 5 ? 8 : 0);
                }

                return (clockMin * perMinute) + 6; // overtime: 5 minutes a period
            }

            case SportKinds.Hockey:
            {
                const double perMinute = 2.0;
                if (g.Period <= 3)
                {
                    var left = ((3 - g.Period) * 20) + (endOfPeriod ? 0 : clockMin);
                    // Intermissions still to come (counting one under way at the end of a period).
                    return (left * perMinute) + ((3 - g.Period) * 18);
                }

                return Math.Max(15, (clockMin * perMinute) + 12); // overtime, maybe a shootout
            }

            case SportKinds.Baseball:
            {
                // "Top 7th", "Mid 7th", "Bot 7th", "End 7th".
                var inning = Math.Max(1, g.Period);
                var d = g.Detail;
                double halvesLeft = d.StartsWith("Top", StringComparison.OrdinalIgnoreCase) ? 1.5
                    : d.StartsWith("Mid", StringComparison.OrdinalIgnoreCase) ? 1
                    : d.StartsWith("Bot", StringComparison.OrdinalIgnoreCase) ? 0.5
                    : d.StartsWith("End", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                halvesLeft += Math.Max(0, 9 - inning) * 2;
                // Extra innings (or the 9th with the home team ahead): at least one more half-inning likely.
                return Math.Max(halvesLeft * 9, inning >= 9 ? 12 : 0) + 3;
            }

            case SportKinds.Soccer:
            {
                // The clock counts up; the detail says "67'" or "45'+2'".
                var played = clockMin;
                var m = Regex.Match(g.Detail, @"^(\d+)'");
                if (m.Success && double.TryParse(m.Groups[1].Value, out var shown))
                {
                    played = shown;
                }

                if (half)
                {
                    return 15 + 45 + 5;
                }

                if (g.Period >= 3)
                {
                    return Math.Max(10, 120 - played + 15); // extra time, maybe penalties
                }

                return Math.Max(0, 90 - played) + 5 + (g.Period <= 1 ? 15 : 0);
            }

            default:
                return null;
        }
    }

    /// <summary>"Q4 2:14", "Halftime", "Final": the scoreboard's own words.</summary>
    public static string Describe(Game g) => string.IsNullOrWhiteSpace(g.Detail) ? g.State : g.Detail;
}
