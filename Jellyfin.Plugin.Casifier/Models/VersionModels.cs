namespace Jellyfin.Plugin.Casifier.Models;

public sealed class VersionReport
{
    public List<MovieVersionGroup> Movies { get; init; } = [];

    /// <summary>Versions skipped because their file couldn't be reached (offline drive, already deleted).</summary>
    public int UnreachableVersions { get; init; }
}

public sealed class MovieVersionGroup
{
    /// <summary>The Jellyfin movie item that holds the best version.</summary>
    public Guid MovieId { get; init; }

    public string Name { get; init; } = string.Empty;

    public int? Year { get; init; }

    public bool HasPrimaryImage { get; init; }

    /// <summary>How the versions were tied together: "Grouped in Jellyfin", "TMDb ID", "IMDb ID", "Title + year".</summary>
    public List<string> MatchedBy { get; init; } = [];

    /// <summary>True when only the title/year links some versions, so a person should confirm it's the same film.</summary>
    public bool TitleMatchOnly { get; init; }

    /// <summary>Versions ordered best first.</summary>
    public List<MovieVersion> Versions { get; init; } = [];
}

public sealed class MovieVersion
{
    /// <summary>Media source id (the version's own item id).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The Jellyfin movie item this version belongs to.</summary>
    public Guid MovieId { get; set; }

    public string Name { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string? Container { get; init; }

    public long? SizeBytes { get; init; }

    public int? Bitrate { get; init; }

    public long? RuntimeTicks { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Nominal resolution, e.g. "2160p", "1080p", "480p", or "Unknown".</summary>
    public string Resolution { get; init; } = "Unknown";

    /// <summary>"uhd", "bluray" or "dvd"; matches the poster banner Casifier draws.</summary>
    public string CaseKey { get; init; } = "dvd";

    public string CaseLabel { get; init; } = "DVD";

    public string? VideoCodec { get; init; }

    public string? VideoRange { get; init; }

    public List<LanguageChip> AudioLanguages { get; init; } = [];

    public List<LanguageChip> SubtitleLanguages { get; init; } = [];

    public List<VersionAudioTrack> AudioTracks { get; init; } = [];

    public List<VersionSubtitleTrack> SubtitleTracks { get; init; } = [];

    /// <summary>Things only this version has; lost if it is deleted.</summary>
    public List<string> UniqueFeatures { get; init; } = [];

    /// <summary>Things that can't be compared automatically and deserve a manual look.</summary>
    public List<string> Cautions { get; init; } = [];

    public bool IsBest { get; init; }

    public bool CanDelete { get; set; }

    public string? DeleteBlockedReason { get; set; }

    /// <summary>True when deleting removes the version's whole movie folder rather than just its file.</summary>
    public bool DeletesFolder { get; set; }

    /// <summary>The file or folder a delete would remove.</summary>
    public string? DeleteTarget { get; set; }
}

public sealed class LanguageChip
{
    public string Name { get; init; } = string.Empty;

    public bool IsUnique { get; init; }
}

public sealed class VersionAudioTrack
{
    public string Language { get; init; } = string.Empty;

    public string? Format { get; init; }

    public string? Channels { get; init; }

    public string? Title { get; init; }

    public bool IsDefault { get; init; }

    public bool IsCommentary { get; init; }

    public bool IsExternal { get; init; }
}

public sealed class VersionSubtitleTrack
{
    public string Language { get; init; } = string.Empty;

    public string? Format { get; init; }

    public string? Title { get; init; }

    public bool IsForced { get; init; }

    public bool IsHearingImpaired { get; init; }

    public bool IsExternal { get; init; }
}

public sealed class VersionDeleteResult
{
    public string Message { get; init; } = string.Empty;

    public List<string> DeletedFiles { get; init; } = [];

    public long FreedBytes { get; init; }
}
