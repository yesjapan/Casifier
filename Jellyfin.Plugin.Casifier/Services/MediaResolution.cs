using Jellyfin.Plugin.Casifier.Models;

namespace Jellyfin.Plugin.Casifier.Services;

public static class MediaResolution
{
    public static CaseKind ResolveCaseKind(int height)
    {
        if (height >= 2160)
        {
            return CaseKind.UltraHd;
        }

        if (height >= 1080)
        {
            return CaseKind.Bluray;
        }

        return CaseKind.Dvd;
    }

    public static string GetCaseLabel(CaseKind caseKind)
        => caseKind switch
        {
            CaseKind.UltraHd => "ULTRA HD",
            CaseKind.Bluray => "BLU-RAY",
            _ => "DVD"
        };

    /// <summary>
    /// Snaps a frame size to its nominal tier so cropped encodes (1920x800, 3840x1600) are
    /// treated as the 1080p / 2160p releases they are rather than by their raw pixel height.
    /// </summary>
    public static int GetTierHeight(int? width, int? height)
    {
        var effective = Math.Max(height ?? 0, (int)Math.Round((width ?? 0) * 9d / 16d));
        return effective switch
        {
            >= 4000 => 4320,
            >= 2000 => 2160,
            >= 1000 => 1080,
            >= 700 => 720,
            _ => effective
        };
    }

    public static bool TryInferHeight(string? value, out int height)
    {
        height = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.ToUpperInvariant();
        if (ContainsAny(normalized, "2160P", "2160", "4K", "UHD", "ULTRAHD", "ULTRA HD"))
        {
            height = 2160;
            return true;
        }

        if (ContainsAny(normalized, "1080P", "1080", "FHD", "FULLHD", "FULL HD", "BLURAY", "BLU-RAY", "BDRIP", "BDREMUX", "BD REMUX"))
        {
            height = 1080;
            return true;
        }

        if (ContainsAny(normalized, "720P", "720"))
        {
            height = 720;
            return true;
        }

        if (ContainsAny(normalized, "DVD", "DVDRIP", "DVD-RIP"))
        {
            height = 480;
            return true;
        }

        return false;
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
