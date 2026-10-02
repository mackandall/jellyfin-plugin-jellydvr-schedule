using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.DvrSchedule.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DvrSchedule;

/// <summary>
/// JellyDVR Schedule: SageTV-style recording priorities. When more recordings are scheduled than
/// there are tuner inputs, the lower-priority one moves to another airing of the same episode or
/// is skipped; "Record this instead" overrides a decision, and skipped airings come back when
/// there's room. The dashboard icon comes from meta.json's <c>imagePath</c>.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // This GUID must match meta.json and pluginUniqueId in Configuration/configPage.html.
    private static readonly Guid PluginId = Guid.Parse("7c1dd9fc-123a-477b-b646-e735ed726d58");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "JellyDVR Schedule";

    public override string Description =>
        "Recording priorities and conflict handling: when more recordings are scheduled than you have tuner " +
        "inputs, moves the lower-priority one to another airing of the same episode, or skips it.";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
