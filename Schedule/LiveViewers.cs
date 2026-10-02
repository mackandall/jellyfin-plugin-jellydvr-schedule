using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>Someone watching a live channel in a Jellyfin client.</summary>
public sealed record LiveViewer(
    string SessionId,
    Guid ChannelId,
    string ChannelName,
    string Device,
    string User,
    string? LiveStreamId,
    DateTime TunedInUtc)
{
    /// <summary>"CNN on Living Room TV".</summary>
    public string Describe() => string.IsNullOrEmpty(Device) ? ChannelName : ChannelName + " on " + Device;
}

/// <summary>
/// Who is watching live TV right now, from Jellyfin's sessions (a client reports playback progress
/// every few seconds; one silent for 2 minutes has gone). Viewers of one channel share one tuner
/// input with each other and with recordings of that channel.
/// </summary>
public sealed class LiveViewers
{
    private static readonly TimeSpan Silence = TimeSpan.FromMinutes(2);

    private readonly ISessionManager _sessions;

    // When each session tuned to its current channel (to pick who gives way: latest first).
    private readonly ConcurrentDictionary<string, (Guid ChannelId, DateTime Since)> _tunedIn = new();

    public LiveViewers(ISessionManager sessions)
    {
        _sessions = sessions;
    }

    public IReadOnlyList<LiveViewer> Current()
    {
        var now = DateTime.UtcNow;
        var result = new List<LiveViewer>();
        foreach (var s in _sessions.Sessions)
        {
            if (s.FullNowPlayingItem is not LiveTvChannel channel || now - s.LastPlaybackCheckIn > Silence)
            {
                continue;
            }

            var since = _tunedIn.AddOrUpdate(
                s.Id,
                _ => (channel.Id, now),
                (_, old) => old.ChannelId == channel.Id ? old : (channel.Id, now)).Since;
            result.Add(new LiveViewer(s.Id, channel.Id, channel.Name, s.DeviceName ?? string.Empty, s.UserName ?? string.Empty, s.PlayState?.LiveStreamId, since));
        }

        foreach (var gone in _tunedIn.Keys.Except(result.Select(v => v.SessionId)).ToList())
        {
            _tunedIn.TryRemove(gone, out _);
        }

        return result;
    }
}
