using System.Globalization;
using Jellyfin.Plugin.Casifier.Models;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Casifier.Services;

public readonly record struct LanguageRef(string Key, string Name)
{
    public static LanguageRef Unknown { get; } = new(string.Empty, "Unknown");

    public bool IsKnown => Key.Length > 0;
}

/// <summary>
/// Compares the versions of one movie: ranks them, and works out what each one has that no
/// other version has (audio languages, subtitle languages, commentaries, a different cut).
/// Pure logic with no library or filesystem access.
/// </summary>
public static class VersionAnalyzer
{
    private static readonly TimeSpan CutTolerance = TimeSpan.FromMinutes(3);

    // PAL DVDs play 23.976 fps film at 25 fps, so they run ~4% short without being a different cut.
    private const double PalSpeedup = 25d / (24000d / 1001d);

    private static readonly string[] CommentaryMarkers =
        ["comment", "kommentar", "commentaire", "comentario", "commento", "コメンタリー", "解説", "评论", "評論", "코멘터리"];

    public static List<MovieVersion> Analyze(IReadOnlyList<MediaSourceInfo> sources, Func<string?, LanguageRef> resolveLanguage)
    {
        var facts = sources.Select(source => Describe(source, resolveLanguage)).ToList();
        // Picture quality first. Between copies of the same quality (two DVDs), keep the one that
        // holds the most: languages and commentaries nobody else has, then languages overall.
        // Bitrate/size only break what's left.
        var ranked = facts
            .OrderByDescending(f => f.TierHeight)
            .ThenByDescending(f => f.RangeRank)
            .ThenByDescending(f => UniqueContentCount(f, facts))
            .ThenByDescending(f => f.AudioLanguages.Where(l => l.IsKnown).Select(l => l.Key).Distinct().Count())
            .ThenByDescending(f => f.SubtitleLanguages.Where(l => l.IsKnown).Select(l => l.Key).Distinct().Count())
            .ThenByDescending(f => f.Source.Bitrate ?? 0)
            .ThenByDescending(f => f.Source.Size ?? 0)
            .ToList();

        var versions = new List<MovieVersion>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            var current = ranked[i];
            var others = ranked.Where(other => !ReferenceEquals(other, current)).ToList();
            versions.Add(Build(current, others, isBest: i == 0, versionCount: ranked.Count));
        }

        return versions;
    }

    private static int UniqueContentCount(VersionFacts f, IReadOnlyList<VersionFacts> all)
    {
        var others = all.Where(other => !ReferenceEquals(other, f)).ToList();
        var otherAudio = others.SelectMany(o => o.AudioLanguages).Select(l => l.Key).ToHashSet();
        var otherSubs = others.SelectMany(o => o.SubtitleLanguages).Select(l => l.Key).ToHashSet();
        var otherCommentary = others.SelectMany(o => o.CommentaryLanguages).Select(l => l.Key).ToHashSet();
        return f.AudioLanguages.Where(l => l.IsKnown).Select(l => l.Key).Distinct().Count(k => !otherAudio.Contains(k))
            + f.SubtitleLanguages.Where(l => l.IsKnown).Select(l => l.Key).Distinct().Count(k => !otherSubs.Contains(k))
            + f.CommentaryLanguages.Select(l => l.Key).Distinct().Count(k => !otherCommentary.Contains(k));
    }

    private static MovieVersion Build(VersionFacts f, List<VersionFacts> others, bool isBest, int versionCount)
    {
        var otherAudio = others.SelectMany(o => o.AudioLanguages).Select(l => l.Key).ToHashSet();
        var otherSubs = others.SelectMany(o => o.SubtitleLanguages).Select(l => l.Key).ToHashSet();
        var otherCommentary = others.SelectMany(o => o.CommentaryLanguages).Select(l => l.Key).ToHashSet();
        var otherRanges = others.Select(o => o.RangeLabel).ToHashSet();

        var uniqueAudio = f.AudioLanguages.Where(l => l.IsKnown && !otherAudio.Contains(l.Key)).ToList();
        var uniqueSubs = f.SubtitleLanguages.Where(l => l.IsKnown && !otherSubs.Contains(l.Key)).ToList();
        var uniqueCommentary = f.CommentaryLanguages.Where(l => !otherCommentary.Contains(l.Key)).ToList();

        var unique = new List<string>();
        unique.AddRange(uniqueAudio.Select(l => $"{l.Name} audio"));
        unique.AddRange(uniqueSubs.Select(l => $"{l.Name} subtitles"));
        unique.AddRange(uniqueCommentary.Select(l => l.IsKnown ? $"{l.Name} commentary" : "Commentary track"));
        if (f.RangeLabel is not null && f.RangeLabel != "SDR" && !otherRanges.Contains(f.RangeLabel))
        {
            unique.Add(f.RangeLabel);
        }

        var cautions = new List<string>();
        var otherRuntimes = others.Where(o => o.Runtime.HasValue).Select(o => o.Runtime!.Value).ToList();
        if (f.Runtime is { } runtime && otherRuntimes.Count > 0 && !otherRuntimes.Any(other => RuntimesMatch(runtime, other)))
        {
            cautions.Add($"Runtime {FormatRuntime(runtime)} doesn't match the other versions ({string.Join(", ", otherRuntimes.Select(FormatRuntime))}); may be a different cut");
        }

        if (f.UnlabeledAudio > 0)
        {
            cautions.Add(f.UnlabeledAudio == 1 ? "1 audio track has no language tag" : $"{f.UnlabeledAudio} audio tracks have no language tag");
        }

        if (f.UnlabeledSubtitles > 0)
        {
            cautions.Add(f.UnlabeledSubtitles == 1 ? "1 subtitle track has no language tag" : $"{f.UnlabeledSubtitles} subtitle tracks have no language tag");
        }

        string? blocked = null;
        if (isBest)
        {
            blocked = "Highest-quality version";
        }
        else if (versionCount < 2)
        {
            blocked = "Only version";
        }
        else if (f.Source.Protocol != MediaProtocol.File || string.IsNullOrWhiteSpace(f.Source.Path))
        {
            blocked = "Not a local file";
        }
        else if (f.Source.VideoType is VideoType.Dvd or VideoType.BluRay)
        {
            blocked = "Disc folder rips must be removed manually";
        }

        var source = f.Source;
        return new MovieVersion
        {
            Id = source.Id ?? string.Empty,
            Name = string.IsNullOrWhiteSpace(source.Name) ? FileNameOf(source.Path) : source.Name,
            FileName = FileNameOf(source.Path),
            Path = source.Path ?? string.Empty,
            Container = source.Container?.Split(',')[0].ToUpperInvariant(),
            SizeBytes = source.Size,
            Bitrate = source.Bitrate,
            RuntimeTicks = source.RunTimeTicks,
            Width = f.Video?.Width,
            Height = f.Video?.Height,
            Resolution = f.TierHeight > 0 ? $"{f.TierHeight}p" : "Unknown",
            CaseKey = f.CaseKind switch { CaseKind.UltraHd => "uhd", CaseKind.Bluray => "bluray", _ => "dvd" },
            CaseLabel = MediaResolution.GetCaseLabel(f.CaseKind),
            VideoCodec = FormatVideoCodec(f.Video?.Codec),
            VideoRange = f.RangeLabel,
            AudioLanguages = f.AudioLanguages
                .Select(l => new LanguageChip { Name = l.Name, IsUnique = uniqueAudio.Contains(l) })
                .ToList(),
            SubtitleLanguages = f.SubtitleLanguages
                .Select(l => new LanguageChip { Name = l.Name, IsUnique = uniqueSubs.Contains(l) })
                .ToList(),
            AudioTracks = f.AudioTracks,
            SubtitleTracks = f.SubtitleTracks,
            UniqueFeatures = unique,
            Cautions = cautions,
            IsBest = isBest,
            CanDelete = blocked is null,
            DeleteBlockedReason = blocked
        };
    }

    private static VersionFacts Describe(MediaSourceInfo source, Func<string?, LanguageRef> resolveLanguage)
    {
        var streams = source.MediaStreams ?? [];
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal && s.Height.HasValue)
            ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

        var tierHeight = MediaResolution.GetTierHeight(video?.Width, video?.Height);
        if (tierHeight == 0)
        {
            // No probed dimensions yet: fall back to hints in the version label / file name.
            foreach (var hint in new[] { source.Name, FileNameOf(source.Path), video?.DisplayTitle })
            {
                if (MediaResolution.TryInferHeight(hint, out var inferred))
                {
                    tierHeight = inferred;
                    break;
                }
            }
        }

        var audioStreams = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        var subtitleStreams = streams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();

        var audioTracks = new List<VersionAudioTrack>();
        var audioLanguages = new List<LanguageRef>();
        var commentaryLanguages = new List<LanguageRef>();
        var unlabeledAudio = 0;
        foreach (var stream in audioStreams)
        {
            var language = resolveLanguage(stream.Language);
            var isCommentary = IsCommentary(stream.Title);
            if (isCommentary)
            {
                AddDistinct(commentaryLanguages, language);
            }
            else if (language.IsKnown)
            {
                AddDistinct(audioLanguages, language);
            }
            else
            {
                unlabeledAudio++;
            }

            audioTracks.Add(new VersionAudioTrack
            {
                Language = language.Name,
                Format = FormatAudio(stream),
                Channels = FormatChannels(stream.Channels),
                Title = string.IsNullOrWhiteSpace(stream.Title) ? null : stream.Title,
                IsDefault = stream.IsDefault,
                IsCommentary = isCommentary,
                IsExternal = stream.IsExternal
            });
        }

        var subtitleTracks = new List<VersionSubtitleTrack>();
        var subtitleLanguages = new List<LanguageRef>();
        var unlabeledSubtitles = 0;
        foreach (var stream in subtitleStreams)
        {
            var language = resolveLanguage(stream.Language);
            if (language.IsKnown)
            {
                AddDistinct(subtitleLanguages, language);
            }
            else
            {
                unlabeledSubtitles++;
            }

            subtitleTracks.Add(new VersionSubtitleTrack
            {
                Language = language.Name,
                Format = stream.Codec?.ToUpperInvariant(),
                Title = string.IsNullOrWhiteSpace(stream.Title) ? null : stream.Title,
                IsForced = stream.IsForced,
                IsHearingImpaired = stream.IsHearingImpaired,
                IsExternal = stream.IsExternal
            });
        }

        var (rangeLabel, rangeRank) = DescribeRange(video);

        return new VersionFacts
        {
            Source = source,
            Video = video,
            TierHeight = tierHeight,
            CaseKind = MediaResolution.ResolveCaseKind(tierHeight),
            RangeLabel = rangeLabel,
            RangeRank = rangeRank,
            Runtime = source.RunTimeTicks is > 0 ? TimeSpan.FromTicks(source.RunTimeTicks.Value) : null,
            AudioLanguages = audioLanguages,
            SubtitleLanguages = subtitleLanguages,
            CommentaryLanguages = commentaryLanguages,
            UnlabeledAudio = unlabeledAudio,
            UnlabeledSubtitles = unlabeledSubtitles,
            AudioTracks = audioTracks,
            SubtitleTracks = subtitleTracks
        };
    }

    private static void AddDistinct(List<LanguageRef> list, LanguageRef language)
    {
        if (!list.Contains(language))
        {
            list.Add(language);
        }
    }

    private static bool IsCommentary(string? title)
        => !string.IsNullOrWhiteSpace(title)
            && CommentaryMarkers.Any(marker => title.Contains(marker, StringComparison.OrdinalIgnoreCase));

    internal static bool RuntimesMatch(TimeSpan a, TimeSpan b)
        => Close(a, b) || Close(a * PalSpeedup, b) || Close(b * PalSpeedup, a);

    private static bool Close(TimeSpan a, TimeSpan b) => (a - b).Duration() <= CutTolerance;

    private static string FormatRuntime(TimeSpan runtime)
        => runtime.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)runtime.TotalHours}h {runtime.Minutes:D2}m")
            : string.Create(CultureInfo.InvariantCulture, $"{runtime.Minutes}m");

    private static (string? Label, int Rank) DescribeRange(MediaStream? video)
    {
        if (video is null)
        {
            return (null, 0);
        }

        var range = video.VideoRangeType.ToString();
        if (range.StartsWith("DOVI", StringComparison.OrdinalIgnoreCase))
        {
            return ("Dolby Vision", 4);
        }

        return range switch
        {
            "HDR10Plus" => ("HDR10+", 3),
            "HDR10" => ("HDR10", 2),
            "HLG" => ("HLG", 1),
            "SDR" => ("SDR", 0),
            _ => (null, 0)
        };
    }

    private static string? FormatVideoCodec(string? codec)
        => codec?.ToLowerInvariant() switch
        {
            null or "" => null,
            "hevc" or "h265" => "HEVC",
            "h264" or "avc" => "H.264",
            "av1" => "AV1",
            "vp9" => "VP9",
            "mpeg2video" => "MPEG-2",
            "mpeg4" => "MPEG-4",
            "vc1" => "VC-1",
            _ => codec.ToUpperInvariant()
        };

    private static string? FormatAudio(MediaStream stream)
    {
        var codec = stream.Codec?.ToLowerInvariant();
        var profile = stream.Profile;
        var name = codec switch
        {
            null or "" => null,
            "truehd" => "TrueHD",
            "eac3" => "Dolby Digital+",
            "ac3" => "Dolby Digital",
            "dts" when profile?.Contains("MA", StringComparison.OrdinalIgnoreCase) == true => "DTS-HD MA",
            "dts" when profile?.Contains("HRA", StringComparison.OrdinalIgnoreCase) == true => "DTS-HD HRA",
            "dts" => "DTS",
            "aac" => "AAC",
            "flac" => "FLAC",
            "opus" => "Opus",
            "mp3" => "MP3",
            "mp2" => "MP2",
            "vorbis" => "Vorbis",
            _ when codec.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
            _ => codec.ToUpperInvariant()
        };

        var spatial = stream.AudioSpatialFormat.ToString();
        if (name is not null && spatial == "DolbyAtmos")
        {
            name += " Atmos";
        }
        else if (name is not null && spatial == "DTSX")
        {
            name += ":X";
        }

        return name;
    }

    private static string? FormatChannels(int? channels)
        => channels switch
        {
            null or <= 0 => null,
            1 => "1.0",
            2 => "2.0",
            3 => "2.1",
            6 => "5.1",
            7 => "6.1",
            8 => "7.1",
            _ => channels.Value.ToString(CultureInfo.InvariantCulture) + "ch"
        };

    private static string FileNameOf(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));

    private sealed class VersionFacts
    {
        public required MediaSourceInfo Source { get; init; }

        public MediaStream? Video { get; init; }

        public int TierHeight { get; init; }

        public CaseKind CaseKind { get; init; }

        public string? RangeLabel { get; init; }

        public int RangeRank { get; init; }

        public TimeSpan? Runtime { get; init; }

        public required List<LanguageRef> AudioLanguages { get; init; }

        public required List<LanguageRef> SubtitleLanguages { get; init; }

        public required List<LanguageRef> CommentaryLanguages { get; init; }

        public int UnlabeledAudio { get; init; }

        public int UnlabeledSubtitles { get; init; }

        public required List<VersionAudioTrack> AudioTracks { get; init; }

        public required List<VersionSubtitleTrack> SubtitleTracks { get; init; }
    }
}
