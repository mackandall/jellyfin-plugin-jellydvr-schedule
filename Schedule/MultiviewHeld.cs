using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>A channel a JellyDVR Live server Multiview holds a tuner for, and the TV showing it.</summary>
public sealed record MultiviewChannel(Guid ChannelId, string Device);

/// <summary>
/// The channels JellyDVR Live's server Multiview has open, from its &lt;data&gt;/jellydvr-live/multiview.json.
/// They hold tuners but have no playback session, so <see cref="LiveViewers"/> can't see them. They give
/// way to recordings on their own (the TV drops a picture), so only the tuner count shows them, not the
/// plan. A file from before this Jellyfin started is left over (Live rewrites it when it starts).
/// </summary>
public static class MultiviewHeld
{
    private static readonly DateTime Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static IReadOnlyList<MultiviewChannel> Current(IApplicationPaths paths)
    {
        var result = new List<MultiviewChannel>();
        try
        {
            var file = Path.Combine(paths.DataPath, "jellydvr-live", "multiview.json");
            if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < Started)
            {
                return result;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var device = s.TryGetProperty("DeviceName", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : string.Empty;
                if (!s.TryGetProperty("ChannelIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var id in ids.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var g))
                    {
                        result.Add(new MultiviewChannel(g, device));
                    }
                }
            }
        }
        catch (Exception)
        {
            // Being rewritten, or not ours to read: counted next time.
        }

        return result;
    }
}
