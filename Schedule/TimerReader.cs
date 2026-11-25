using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DvrSchedule.Schedule;

/// <summary>
/// Jellyfin's timers without the cost of ILiveTvManager.GetTimers, which builds a full programme
/// DTO (every field, series images) for each timer, about 40 ms apiece, and also reads the hundreds
/// of cancelled series episodes Jellyfin keeps. Nothing here uses ProgramInfo; the rest of
/// TimerInfoDto is filled as LiveTvDtoService.GetTimerInfoDto fills it.
/// </summary>
internal static class TimerReader
{
    /// <summary>Upcoming and running recordings, soonest first.</summary>
    public static Task<List<TimerInfoDto>> LiveAsync(IServiceProvider services, CancellationToken ct) =>
        ReadAsync(services, s => s is RecordingStatus.New or RecordingStatus.InProgress, ct);

    /// <summary>What GetTimers(new TimerQuery()) returns (all but completed), soonest first.</summary>
    public static Task<List<TimerInfoDto>> AllAsync(IServiceProvider services, CancellationToken ct) =>
        ReadAsync(services, _ => true, ct);

    private static async Task<List<TimerInfoDto>> ReadAsync(IServiceProvider services, Func<RecordingStatus, bool> wanted, CancellationToken ct)
    {
        var liveTv = services.GetRequiredService<ILiveTvManager>();
        var library = services.GetRequiredService<ILibraryManager>();
        var list = new List<TimerInfoDto>();
        var channels = new Dictionary<Guid, string?>();
        foreach (var service in liveTv.Services)
        {
            IEnumerable<TimerInfo> raw;
            try
            {
                raw = await service.GetTimersAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue; // as GetTimers: a failing service is left out
            }

            foreach (var t in raw.Where(t => t.Status != RecordingStatus.Completed && wanted(t.Status)))
            {
                list.Add(Light(library, service.Name, t, channels));
            }
        }

        return list.OrderBy(t => t.StartDate).ToList();
    }

    // The fields of Jellyfin's TimerInfoDto (LiveTvDtoService.GetTimerInfoDto) without ProgramInfo.
    private static TimerInfoDto Light(ILibraryManager library, string serviceName, TimerInfo info, Dictionary<Guid, string?> channels)
    {
        var channelId = library.GetNewItemId((serviceName + info.ChannelId + "4").ToLowerInvariant(), typeof(LiveTvChannel));
        if (!channels.TryGetValue(channelId, out var channelName))
        {
            channelName = channels[channelId] = string.IsNullOrEmpty(info.ChannelId) ? null : library.GetItemById(channelId)?.Name;
        }

        return new TimerInfoDto
        {
            Id = InternalId(info.Id).ToString("N"),
            ExternalId = info.Id,
            Name = info.Name,
            Overview = info.Overview,
            StartDate = info.StartDate,
            EndDate = info.EndDate,
            Status = info.Status,
            ChannelId = channelId,
            ExternalChannelId = info.ChannelId,
            ChannelName = channelName,
            SeriesTimerId = string.IsNullOrEmpty(info.SeriesTimerId) ? null : InternalId(info.SeriesTimerId).ToString("N"),
            ExternalSeriesTimerId = info.SeriesTimerId,
            ProgramId = string.IsNullOrEmpty(info.ProgramId) ? null
                : library.GetNewItemId(("Emby" + info.ProgramId + "4").ToLowerInvariant(), typeof(LiveTvProgram)).ToString("N"),
            ExternalProgramId = info.ProgramId,
            PrePaddingSeconds = info.PrePaddingSeconds,
            PostPaddingSeconds = info.PostPaddingSeconds,
            IsPrePaddingRequired = info.IsPrePaddingRequired,
            IsPostPaddingRequired = info.IsPostPaddingRequired,
            KeepUntil = info.KeepUntil,
            Priority = info.Priority,
            ServiceName = serviceName,
            RunTimeTicks = (info.EndDate - info.StartDate).Ticks,
        };
    }

    // LiveTvDtoService's timer and series-timer ids: MD5 of the UTF-16 text, as a Guid.
    private static Guid InternalId(string externalId) =>
        new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.Unicode.GetBytes(("Emby" + externalId + "4").ToLowerInvariant())));
}
