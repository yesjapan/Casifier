using Jellyfin.Plugin.Casifier.Configuration;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Casifier;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Casifier";

    public override Guid Id => Guid.Parse("6de807d7-041d-4592-a278-91f04a37ec0f");

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        // The settings page must stay first: Jellyfin opens the first page from the plugin list.
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetResourcePath("configPage.html")
            },
            new PluginPageInfo
            {
                Name = "CasifierVersions",
                DisplayName = "Movie Versions",
                EmbeddedResourcePath = GetResourcePath("versionsPage.html"),
                EnableInMainMenu = true,
                MenuIcon = "video_library"
            }
        ];
    }

    private string GetResourcePath(string fileName)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0}.Configuration.{1}",
            GetType().Namespace,
            fileName);
}
