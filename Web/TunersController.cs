using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DvrSchedule.Schedule;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DvrSchedule.Web;

/// <summary>
/// What each tuner input is busy with, so a client whose live channel failed to start can say
/// "all tuners are recording X, Y, Z" (and offer those channels, which cost no extra input)
/// instead of a generic playback error. Plus the web script that does that.
/// </summary>
[ApiController]
[Route("JellyDvrSchedule")]
public sealed class TunersController : ControllerBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _paths;
    private readonly LiveViewers _viewers;
    private readonly ActiveRecordings _active;

    public TunersController(IServiceProvider serviceProvider, ILibraryManager libraryManager, IApplicationPaths paths, LiveViewers viewers, ActiveRecordings active)
    {
        _active = active;
        _serviceProvider = serviceProvider;
        _libraryManager = libraryManager;
        _paths = paths;
        _viewers = viewers;
    }

    /// <param name="channelId">The channel someone tried to watch (optional).</param>
    [HttpGet("Tuners")]
    [Authorize(Policy = Policies.LiveTvAccess)]
    public IActionResult Tuners([FromQuery] Guid? channelId)
    {
        // Answered while a client waits to start a channel: no ILiveTvManager.GetTimers (seconds).
        var capacity = Plugin.Instance?.Configuration.TunerInputs ?? 0;
        var buffers = LiveBufferTimerIds();
        var timers = _active.Current();

        var busy = new Dictionary<Guid, Busy>();
        Busy For(Guid id)
        {
            if (!busy.TryGetValue(id, out var b))
            {
                var ch = _libraryManager.GetItemById(id) as LiveTvChannel;
                b = busy[id] = new Busy { ChannelId = id, ChannelName = ch?.Name ?? string.Empty, Number = ch?.Number ?? string.Empty };
            }

            return b;
        }

        foreach (var t in timers)
        {
            var b = For(t.ChannelId);
            var ends = t.EndUtc;
            if (buffers.Contains(t.TimerId))
            {
                b.Buffering = true; // JellyDVR Live buffer: shares a viewer's stream
            }
            else
            {
                b.Recording.Add(t.Name);
                b.EndsUtc = b.EndsUtc is { } e && e > ends ? e : ends;
            }
        }

        foreach (var v in _viewers.Current())
        {
            For(v.ChannelId).Watching.Add(string.IsNullOrEmpty(v.Device) ? v.User : v.Device);
        }

        var list = busy.Values.OrderBy(b => b.EndsUtc ?? DateTime.MaxValue).ToList();
        var requested = channelId is { } cid ? _libraryManager.GetItemById(cid) as LiveTvChannel : null;
        return Ok(new
        {
            Capacity = capacity,
            InUse = list.Count,
            // Watching the requested channel needs an input only if nothing has it open already.
            Full = capacity > 0 && list.Count >= capacity && (requested is null || !busy.ContainsKey(requested.Id)),
            RequestedChannel = requested?.Name,
            IsChannel = requested is not null,
            Busy = list
        });
    }

    [HttpGet("web/tuners.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public Task<ContentResult> Script() => Embedded("tuners.js");

    /// <summary>The Favourites list under Live TV > Series; tuners.js loads it.</summary>
    [HttpGet("web/favourites.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public Task<ContentResult> FavouritesScript() => Embedded("favourites.js");

    private async Task<ContentResult> Embedded(string name)
    {
        var assembly = typeof(TunersController).Assembly;
        await using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Web." + name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream);
        return Content(await reader.ReadToEndAsync().ConfigureAwait(false), "application/javascript");
    }

    public sealed class Busy
    {
        public Guid ChannelId { get; set; }

        public string ChannelName { get; set; } = string.Empty;

        public string Number { get; set; } = string.Empty;

        public List<string> Recording { get; } = new();

        public List<string> Watching { get; } = new();

        public bool Buffering { get; set; }

        public DateTime? EndsUtc { get; set; }
    }

    private HashSet<string> LiveBufferTimerIds()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var file = Path.Combine(_paths.DataPath, "jellydvr-live", "buffers.json");
            if (System.IO.File.Exists(file))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                foreach (var b in doc.RootElement.EnumerateArray())
                {
                    if (b.TryGetProperty("TimerId", out var id) && id.ValueKind == JsonValueKind.String
                        && !(b.TryGetProperty("Kept", out var kept) && kept.ValueKind == JsonValueKind.True))
                    {
                        result.Add(id.GetString()!);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return result;
    }
}
