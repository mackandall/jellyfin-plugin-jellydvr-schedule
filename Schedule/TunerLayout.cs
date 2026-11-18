using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// Which tuner device each channel uses and how many inputs each device has. Channels only
/// compete for inputs with channels on the same device: four recordings on the HDHomeRun and
/// one on BroadcastHub are not five on one device.
/// </summary>
public sealed class TunerLayout
{
    /// <summary>The pool of channels no device lists (or every channel, when counting a total).</summary>
    public const string OtherPool = "other";

    private readonly Dictionary<Guid, string> _poolOf;

    public TunerLayout(IReadOnlyList<TunerPool> pools, Dictionary<Guid, string> poolOf, string defaultPool)
    {
        Pools = pools;
        _poolOf = poolOf;
        DefaultPool = defaultPool;
    }

    /// <summary>One device (or, counting a total, all of them): inputs &lt;= 0 means no limit.</summary>
    public sealed record TunerPool(string Id, string Name, int Inputs, int JellyfinInputs, int Channels, int? DeviceInputs = null);

    public IReadOnlyList<TunerPool> Pools { get; }

    public string DefaultPool { get; }

    /// <summary>Everything on one pool of this many inputs (the old "Tuner inputs" behaviour).</summary>
    public static TunerLayout Single(int inputs, string name = "All tuners") =>
        new(new[] { new TunerPool(OtherPool, name, inputs, inputs, 0) }, new Dictionary<Guid, string>(), OtherPool);

    public string PoolOf(Guid channelId) => _poolOf.TryGetValue(channelId, out var p) ? p : DefaultPool;

    public TunerPool? Pool(string id) => Pools.FirstOrDefault(p => p.Id == id);

    /// <summary>Inputs of the pool, or 0 when it has no limit.</summary>
    public int InputsOf(string pool) => Pool(pool) is { Inputs: > 0 } p ? p.Inputs : 0;

    public int InputsFor(Guid channelId) => InputsOf(PoolOf(channelId));

    public string NameFor(Guid channelId) => Pool(PoolOf(channelId))?.Name ?? "tuner";

    /// <summary>Any pool with a limit: otherwise conflicts can't be found.</summary>
    public bool HasLimit => Pools.Any(p => p.Inputs > 0);

    /// <summary>All inputs added up (0 when any device has no limit), for one-number summaries.</summary>
    public int TotalInputs
    {
        get
        {
            var devices = Pools.Where(p => p.Id != OtherPool || Pools.Count == 1).ToList();
            return devices.Count == 0 || devices.Any(p => p.Inputs <= 0) ? 0 : devices.Sum(p => p.Inputs);
        }
    }

    /// <summary>"HDHomeRun CONNECT QUATRO 4 · BroadcastHub 5 · FastChannels no limit".</summary>
    public string Describe() => string.Join(" · ", Pools
        .Where(p => p.Id != OtherPool || Pools.Count == 1)
        .Select(p => p.Name + " " + (p.Inputs > 0 ? p.Inputs.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no limit")));

    /// <summary>
    /// Whether these channels (each needing one input; the same channel twice shares one) need
    /// more inputs than some device has. Returns the overloaded pool, or null.
    /// </summary>
    public string? Overloaded(IEnumerable<Guid> channels)
    {
        foreach (var g in channels.Distinct().GroupBy(PoolOf))
        {
            var inputs = InputsOf(g.Key);
            if (inputs > 0 && g.Count() > inputs)
            {
                return g.Key;
            }
        }

        return null;
    }
}

/// <summary>Builds (and caches) the <see cref="TunerLayout"/> from Jellyfin's tuner devices and the settings.</summary>
public sealed class TunerLayouts
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(15);
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfigurationManager _config;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TunerLayouts> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, string> _hostOfChannel = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _channelsPerHost = new(StringComparer.Ordinal);
    private Dictionary<Guid, string> _poolOf = new();
    private Dictionary<string, int> _reported = new(StringComparer.Ordinal);
    private Dictionary<string, string> _reportedNames = new(StringComparer.Ordinal);
    private string? _lastSummary;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private DateTime _scannedUtc = DateTime.MinValue;
    private TunerLayout? _last;

    public TunerLayouts(IServiceProvider serviceProvider, IConfigurationManager config, ILibraryManager libraryManager, ILogger<TunerLayouts> logger)
    {
        _serviceProvider = serviceProvider;
        _config = config;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>The layout, re-reading the devices' channel lists when they are stale.</summary>
    public async Task<TunerLayout> GetAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _scannedUtc > Fresh)
        {
            await ScanAsync(ct).ConfigureAwait(false);
        }

        return _last = Build(Config);
    }

    /// <summary>
    /// The layout without waiting on the devices (for requests a client is waiting on): the last
    /// scan, refreshed in the background when stale.
    /// </summary>
    public TunerLayout Current()
    {
        if (DateTime.UtcNow - _scannedUtc > Fresh)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ScanAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Tuner device scan failed");
                }
            });
        }

        return _last = Build(Config);
    }

    /// <summary>Forget the last scan (the settings or Jellyfin's devices changed).</summary>
    public void Invalidate() => _scannedUtc = DateTime.MinValue;

    // Asks each tuner type for its channels: every ChannelInfo names the device (TunerHostId).
    private async Task ScanAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _scannedUtc <= Fresh)
            {
                return;
            }

            var hostOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var manager = _serviceProvider.GetService<ITunerHostManager>();
            foreach (var type in manager?.TunerHosts ?? Array.Empty<ITunerHost>())
            {
                try
                {
                    foreach (var ch in await type.GetChannels(true, ct).ConfigureAwait(false))
                    {
                        if (string.IsNullOrEmpty(ch.Id) || string.IsNullOrEmpty(ch.TunerHostId))
                        {
                            continue;
                        }

                        // Jellyfin tunes a channel listed by two devices on the first one listing it.
                        if (hostOf.TryAdd(ch.Id, ch.TunerHostId))
                        {
                            counts[ch.TunerHostId] = counts.GetValueOrDefault(ch.TunerHostId) + 1;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not list the channels of {Type} tuners", type.Type);
                }
            }

            // What each HDHomeRun-type device says it has now (discover.json): Jellyfin keeps the
            // count from when the tuner was added, and a dashboard re-save sets it to 0.
            var reported = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var h in (_config.GetConfiguration<LiveTvOptions>("livetv").TunerHosts ?? Array.Empty<TunerHostInfo>())
                .Where(h => string.Equals(h.Type, "hdhomerun", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(h.Url)))
            {
                try
                {
                    var url = h.Url.Contains("://", StringComparison.Ordinal) ? h.Url : "http://" + h.Url;
                    var json = await Http.GetStringAsync(url.TrimEnd('/') + "/discover.json", ct).ConfigureAwait(false);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("FriendlyName", out var fn) && fn.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(fn.GetString()))
                    {
                        names[h.Id] = fn.GetString()!.Trim();
                    }

                    if (doc.RootElement.TryGetProperty("TunerCount", out var tc) && tc.TryGetInt32(out var n) && n > 0)
                    {
                        reported[h.Id] = n;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "No tuner count from {Url}", h.Url);
                    if (_reported.TryGetValue(h.Id, out var last))
                    {
                        reported[h.Id] = last; // keep the last answer through a blip
                    }

                    if (_reportedNames.TryGetValue(h.Id, out var lastName))
                    {
                        names[h.Id] = lastName;
                    }
                }
            }

            _reported = reported;
            _reportedNames = names;

            if (hostOf.Count > 0 || _hostOfChannel.Count == 0)
            {
                // Jellyfin's channel items carry the tuner's channel id as ExternalId.
                var poolOf = new Dictionary<Guid, string>();
                if (hostOf.Count > 0)
                {
                    foreach (var ch in _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel } }))
                    {
                        if (!string.IsNullOrEmpty(ch.ExternalId) && hostOf.TryGetValue(ch.ExternalId, out var host))
                        {
                            poolOf[ch.Id] = host;
                        }
                    }
                }

                var summary = poolOf.Count + ": " + string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + "=" + c.Value));
                _hostOfChannel = hostOf;
                _channelsPerHost = counts;
                _poolOf = poolOf;

                // Rescanned every 15 minutes: only say so when the mapping changed.
                if (summary != _lastSummary)
                {
                    _lastSummary = summary;
                    _logger.LogInformation(
                        "Tuner devices: {Count} channel(s) mapped ({Hosts})",
                        poolOf.Count,
                        string.Join(", ", counts.Select(c => c.Key + "=" + c.Value)));
                }
            }

            _scannedUtc = DateTime.UtcNow;
        }
        finally
        {
            _lock.Release();
        }
    }

    private TunerLayout Build(PluginConfiguration cfg)
    {
        var hosts = _config.GetConfiguration<LiveTvOptions>("livetv").TunerHosts ?? Array.Empty<TunerHostInfo>();
        if (!string.Equals(cfg.TunerCounting, PluginConfiguration.CountPerDevice, StringComparison.OrdinalIgnoreCase))
        {
            return TunerLayout.Single(cfg.TunerInputs > 0 ? cfg.TunerInputs : ScheduleManager.JellyfinTunerCount(_config));
        }

        if (_poolOf.Count == 0)
        {
            // Devices not scanned yet (or none answered): plan as one total until they are.
            return TunerLayout.Single(cfg.TunerInputs > 0 ? cfg.TunerInputs : ScheduleManager.JellyfinTunerCount(_config));
        }

        var pools = new List<TunerLayout.TunerPool>();
        foreach (var h in hosts)
        {
            var setting = (cfg.TunerDevices ?? Array.Empty<TunerDeviceSetting>()).FirstOrDefault(d => d.HostId == h.Id);
            int? said = _reported.TryGetValue(h.Id, out var r) ? r : null;
            var inputs = setting is { Inputs: > 0 } ? setting.Inputs : setting is { Inputs: < 0 } ? 0 : said ?? h.TunerCount;
            var name = string.IsNullOrWhiteSpace(h.FriendlyName) && _reportedNames.TryGetValue(h.Id, out var n) ? n : DeviceName(h);
            pools.Add(new TunerLayout.TunerPool(h.Id, name, inputs, h.TunerCount, _channelsPerHost.GetValueOrDefault(h.Id), said));
        }

        // Channels no device lists (gone, or the scan failed): no limit, rather than a guess.
        pools.Add(new TunerLayout.TunerPool(TunerLayout.OtherPool, "Other channels", 0, 0, 0));

        return new TunerLayout(pools, _poolOf, TunerLayout.OtherPool);
    }

    /// <summary>The name shown for a device: its friendly name, else its address.</summary>
    public static string DeviceName(TunerHostInfo h)
    {
        if (!string.IsNullOrWhiteSpace(h.FriendlyName))
        {
            return h.FriendlyName.Trim();
        }

        var url = h.Url ?? string.Empty;
        var kind = string.Equals(h.Type, "hdhomerun", StringComparison.OrdinalIgnoreCase) ? "HDHomeRun" : string.Equals(h.Type, "m3u", StringComparison.OrdinalIgnoreCase) ? "M3U" : h.Type;
        return Uri.TryCreate(url.Contains("://", StringComparison.Ordinal) ? url : "http://" + url, UriKind.Absolute, out var u)
            ? $"{kind} {u.Host}{(u.IsDefaultPort ? string.Empty : ":" + u.Port)}"
            : $"{kind} {url}";
    }
}
