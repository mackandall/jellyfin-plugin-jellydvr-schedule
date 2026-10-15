using Jellyfin.Plugin.DvrSchedule.Schedule;
using Jellyfin.Plugin.DvrSchedule.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DvrSchedule;

/// <summary>
/// Registers plugin services into Jellyfin's DI container at startup.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<OutcomeStore>();
        serviceCollection.AddSingleton<GuideMarks>();
        serviceCollection.AddSingleton<ActiveRecordings>();
        serviceCollection.AddSingleton<Favourites>();
        serviceCollection.AddSingleton<ScheduleManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<ScheduleManager>());
        serviceCollection.AddSingleton<LiveViewers>();
        serviceCollection.AddSingleton<LiveViewerGuard>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<LiveViewerGuard>());
        serviceCollection.AddSingleton<RecordingStartWatchdog>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<RecordingStartWatchdog>());
        serviceCollection.AddSingleton<RecordingStallWatchdog>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<RecordingStallWatchdog>());

        // Jellyfin builds its MVC ApplicationPartManager from its own assembly before any
        // plugin loads and never re-scans, so a plugin's controllers are silently absent
        // unless it adds itself explicitly.
        serviceCollection.AddControllers().AddApplicationPart(typeof(ScheduleController).Assembly);
    }
}
