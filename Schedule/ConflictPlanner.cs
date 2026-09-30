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
    TimeSpan PadAfter);

/// <summary>An alternative airing of the same episode that a recording could move to.</summary>
public sealed record Airing(Guid ProgramId, Guid ChannelId, string ChannelName, DateTime Start, DateTime End);

public enum DecisionKind
{
    /// <summary>Record another airing of the same episode instead.</summary>
    Move,

    /// <summary>Don't record it (no other airing fits).</summary>
    Skip,

    /// <summary>Overloaded, but every recording in the slot is already recording: nothing to do.</summary>
    Unresolvable
}

public sealed record Decision(
    DecisionKind Kind,
    PlannedRecording Recording,
    Airing? MoveTo,
    DateTime SlotStart,
    DateTime SlotEnd,
    IReadOnlyList<PlannedRecording> Competing);

/// <summary>
/// Finds the times when more recordings are scheduled than there are tuner inputs and decides,
/// lowest priority first, which recordings give way: moved to another airing of the same
/// episode when one fits, otherwise skipped. Recordings on the same channel at the same time
/// share one input. Pure logic, no Jellyfin calls.
/// </summary>
public static class ConflictPlanner
{
    public static List<Decision> Plan(
        IReadOnlyList<PlannedRecording> recordings,
        int capacity,
        bool moveToOtherAiring,
        Func<PlannedRecording, IEnumerable<Airing>> alternatives)
    {
        var decisions = new List<Decision>();
        if (capacity <= 0)
        {
            return decisions;
        }

        var active = recordings.ToList();
        var usedAirings = new HashSet<Guid>(recordings.Select(r => r.ProgramId));
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
            if (moveToOtherAiring)
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
