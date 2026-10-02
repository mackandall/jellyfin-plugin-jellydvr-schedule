using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>One scheduled (or candidate) recording as the planner sees it.</summary>
public sealed record PlannedRecording(
    string Key,
    string Title,
    Guid ChannelId,
    string ChannelName,
    DateTime Start,
    DateTime End,
    int Rank,
    bool Locked,
    string? ShowId,
    Guid ProgramId,
    string? TimerId,
    string? SeriesTimerId,
    TimeSpan PadBefore,
    TimeSpan PadAfter,
    bool IsRepeat = false,
    bool OneOff = false,
    bool Pinned = false);

/// <summary>An alternative airing of the same episode that a recording could move to.</summary>
public sealed record Airing(Guid ProgramId, Guid ChannelId, string ChannelName, DateTime Start, DateTime End);

public enum DecisionKind
{
    /// <summary>Record another airing of the same episode instead.</summary>
    Move,

    /// <summary>Don't record it (no other airing fits).</summary>
    Skip,

    /// <summary>Overloaded, but every recording in the slot is already recording: nothing to do.</summary>
    Unresolvable,

    /// <summary>The same episode is also scheduled at another time: this copy is dropped.</summary>
    Duplicate,

    /// <summary>An airing skipped earlier now fits again: put it back on the schedule.</summary>
    Restore
}

public sealed record Decision(
    DecisionKind Kind,
    PlannedRecording Recording,
    Airing? MoveTo,
    DateTime SlotStart,
    DateTime SlotEnd,
    IReadOnlyList<PlannedRecording> Competing,
    PlannedRecording? KeptCopy = null);

/// <summary>
/// Finds the times when more recordings are scheduled than there are tuner inputs and decides,
/// lowest priority first, which recordings give way: moved to another airing of the same
/// episode when one fits, otherwise skipped. Recordings on the same channel at the same time
/// share one input. Pure logic, no Jellyfin calls.
/// </summary>
public static class ConflictPlanner
{
    /// <summary>
    /// A guide id that identifies one episode or film (TMS "EP…"/"MV…"), as opposed to a whole show
    /// ("SH…", used for news and talk shows without episode data), which must never be treated as
    /// "the same episode".
    /// </summary>
    public static bool IsEpisodeId(string? showId) =>
        showId is { Length: > 4 }
        && (showId.StartsWith("EP", StringComparison.OrdinalIgnoreCase) || showId.StartsWith("MV", StringComparison.OrdinalIgnoreCase));

    public static List<Decision> Plan(
        IReadOnlyList<PlannedRecording> recordings,
        int capacity,
        bool moveToOtherAiring,
        Func<PlannedRecording, IEnumerable<Airing>> alternatives,
        bool recordEachEpisodeOnce = false,
        bool preferFirstRun = false,
        IReadOnlyList<PlannedRecording>? restorable = null)
    {
        var decisions = new List<Decision>();
        var active = recordings.ToList();

        // One copy per episode: keep one already recording, else a hand-picked one-off, else the
        // first run (if preferred) or the earliest airing; drop the rest. Dropped airings stay
        // available as places to move to if the kept one loses a conflict.
        if (recordEachEpisodeOnce)
        {
            foreach (var group in active.Where(r => IsEpisodeId(r.ShowId)).GroupBy(r => r.ShowId!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
            {
                var kept = group
                    .OrderByDescending(r => r.Locked)
                    .ThenByDescending(r => r.Pinned)
                    .ThenByDescending(r => r.OneOff)
                    .ThenBy(r => preferFirstRun && r.IsRepeat)
                    .ThenBy(r => r.Start)
                    .First();
                foreach (var extra in group.Where(r => r != kept && !r.Locked))
                {
                    active.Remove(extra);
                    decisions.Add(new Decision(DecisionKind.Duplicate, extra, null, extra.Start, extra.End, new[] { kept }, kept));
                }
            }
        }

        if (capacity <= 0)
        {
            return decisions;
        }


        var usedAirings = new HashSet<Guid>(active.Select(r => r.ProgramId));
        for (var guard = 0; guard < 200; guard++)
        {
            if (FirstOverload(active, capacity, DateTime.MinValue, DateTime.MaxValue) is not { } slot)
            {
                break;
            }

            var loser = slot.Members
                .Where(r => !r.Locked)
                .OrderBy(r => r.Rank)
                .ThenByDescending(r => r.Start)
                .FirstOrDefault();
            if (loser is null)
            {
                // Everything in this slot is already recording: nothing can give way. Note it and
                // leave those recordings out of further planning so the search moves on.
                decisions.Add(new Decision(DecisionKind.Unresolvable, slot.Members[0], null, slot.Start, slot.End, slot.Members));
                active.RemoveAll(r => slot.Members.Contains(r));
                continue;
            }

            active.Remove(loser);
            Airing? moveTo = null;
            // Only a real episode id says two airings are the same episode, and never move to
            // an episode that is already being recorded at another time. An airing someone chose
            // by hand ("Record this instead") is never swapped for a different one.
            if (moveToOtherAiring && !loser.Pinned && IsEpisodeId(loser.ShowId)
                && !active.Any(r => string.Equals(r.ShowId, loser.ShowId, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var airing in alternatives(loser).Where(a => !usedAirings.Contains(a.ProgramId)).OrderBy(a => a.Start))
                {
                    var candidate = loser with
                    {
                        Key = loser.Key + ">" + airing.ProgramId.ToString("N"),
                        ChannelId = airing.ChannelId,
                        ChannelName = airing.ChannelName,
                        Start = airing.Start - loser.PadBefore,
                        End = airing.End + loser.PadAfter,
                        ProgramId = airing.ProgramId,
                        TimerId = null,
                        Locked = false
                    };
                    var trial = active.Append(candidate).ToList();
                    if (FirstOverload(trial, capacity, candidate.Start, candidate.End) is null)
                    {
                        moveTo = airing;
                        active.Add(candidate);
                        usedAirings.Add(airing.ProgramId);
                        break;
                    }
                }
            }

            decisions.Add(new Decision(moveTo is null ? DecisionKind.Skip : DecisionKind.Move, loser, moveTo, slot.Start, slot.End, slot.Members));
        }

        // Airings skipped earlier go back on, highest priority first, wherever they fit now
        // without overloading anything (an episode already scheduled at another time stays off).
        foreach (var r in (restorable ?? Array.Empty<PlannedRecording>()).OrderByDescending(r => r.Rank).ThenBy(r => r.Start))
        {
            if (IsEpisodeId(r.ShowId) && active.Any(a => string.Equals(a.ShowId, r.ShowId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var trial = active.Append(r).ToList();
            if (FirstOverload(trial, capacity, r.Start, r.End) is null)
            {
                active.Add(r);
                decisions.Add(new Decision(DecisionKind.Restore, r, null, r.Start, r.End, Array.Empty<PlannedRecording>()));
            }
        }

        return decisions;
    }

    /// <summary>The first stretch of time (within [from, to)) needing more inputs than there are.</summary>
    public static (DateTime Start, DateTime End, List<PlannedRecording> Members)? FirstOverload(
        IReadOnlyList<PlannedRecording> recordings, int capacity, DateTime from, DateTime to)
    {
        var points = recordings.SelectMany(r => new[] { r.Start, r.End })
            .Where(t => t > from && t < to)
            .ToList();
        if (from > DateTime.MinValue)
        {
            points.Add(from);
        }

        if (to < DateTime.MaxValue)
        {
            points.Add(to);
        }

        points = points.Distinct().OrderBy(t => t).ToList();
        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            var members = recordings.Where(r => r.Start < b && r.End > a).ToList();
            // Same channel at the same time shares one input.
            if (members.Select(r => r.ChannelId).Distinct().Count() > capacity)
            {
                return (a, b, members);
            }
        }

        return null;
    }
}
