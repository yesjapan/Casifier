using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Casifier.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public bool RunAfterLibraryScan { get; set; } = true;

    public bool ProcessMoviesOnly { get; set; } = true;

    public bool OverwritePrimaryPoster { get; set; } = true;

    public string BackupSuffix { get; set; } = ".casifier-original";
}
