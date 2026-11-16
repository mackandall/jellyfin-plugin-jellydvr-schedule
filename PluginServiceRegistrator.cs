using Jellyfin.Plugin.DvrSchedule.Alerts;
using Jellyfin.Plugin.DvrSchedule.Overruns;
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
        serviceCollection.AddSingleton<AlertSenders>();
        serviceCollection.AddSingleton<AlertCenter>();
        serviceCollection.AddSingleton<AlertService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<AlertService>());
        serviceCollection.AddSingleton<OutcomeStore>();
        serviceCollection.AddSingleton<PauseStore>();
        serviceCollection.AddSingleton<RuleBoard>();
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
        serviceCollection.AddSingleton<Scoreboard>();
        serviceCollection.AddSingleton<OverrunStore>();
        serviceCollection.AddSingleton<FollowUpJoiner>();
        serviceCollection.AddSingleton<OverrunWatcher>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<OverrunWatcher>());

        // Jellyfin builds its MVC ApplicationPartManager from its own assembly before any
        // plugin loads and never re-scans, so a plugin's controllers are silently absent
        // unless it adds itself explicitly.
        serviceCollection.AddControllers().AddApplicationPart(typeof(ScheduleController).Assembly);
    }
}
