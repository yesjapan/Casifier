using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Casifier.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Casifier.Services;

public sealed class MovieVersionService
{
    private static readonly HashSet<string> UndeterminedLanguages =
        new(["und", "unk", "mis", "mul", "zxx", "unknown", "undefined", "undetermined"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> VideoExtensions = new(
        [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".vob", ".iso",
         ".webm", ".flv", ".divx", ".ogm", ".ogv", ".3gp", ".img", ".rmvb", ".asf", ".wtv", ".dvr-ms", ".strm"],
        StringComparer.OrdinalIgnoreCase);

    // Refuse to wipe a folder whose tree is unexpectedly large; that is not a single-movie folder.
    private const int MaxFolderEntries = 2000;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<MovieVersionService> _logger;

    public MovieVersionService(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ILocalizationManager localization,
        ILogger<MovieVersionService> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _localization = localization;
        _logger = logger;
    }

    public VersionReport GetReport()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var context = new AnalysisContext(CreateLanguageResolver(), GetLibraryRoots());
        var groups = new List<MovieVersionGroup>();
        var unreachable = 0;

        foreach (var cluster in FindClusters())
        {
            try
            {
                var analyzed = AnalyzeCluster(cluster, context);
                unreachable += analyzed.Unreachable;
                if (analyzed.Group is not null)
                {
                    groups.Add(analyzed.Group);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Casifier could not compare versions of {MovieName}", cluster.Movies[0].Name);
            }
        }

        _logger.LogInformation(
            "Casifier found {Count} movies with more than one copy in {Elapsed} ms ({Unreachable} copies on offline drives skipped)",
            groups.Count,
            stopwatch.ElapsedMilliseconds,
            unreachable);

        return new VersionReport
        {
            Movies = groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Year).ToList(),
            UnreachableVersions = unreachable
        };
    }

    public VersionDeleteOutcome DeleteVersion(Guid movieId, string versionId)
    {
        // Re-derive the whole comparison server side so the UI can never delete something the
        // analysis would not offer: the best copy, the last copy, a disc folder, a shared folder.
        var cluster = FindClusters().FirstOrDefault(c => c.Movies.Any(m => m.Id.Equals(movieId)));
        if (cluster is null)
        {
            return VersionDeleteOutcome.NotFound("That movie no longer has more than one version.");
        }

        var analyzed = AnalyzeCluster(cluster, new AnalysisContext(CreateLanguageResolver(), GetLibraryRoots()));
        var version = analyzed.Group?.Versions.FirstOrDefault(v =>
            v.MovieId.Equals(movieId) && string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));
        if (analyzed.Group is null || version is null)
        {
            return VersionDeleteOutcome.NotFound("That version no longer exists. It may already have been deleted.");
        }

        if (!version.CanDelete || string.IsNullOrEmpty(version.DeleteTarget))
        {
            return VersionDeleteOutcome.Conflict($"This version can't be deleted: {version.DeleteBlockedReason}.");
        }

        var owner = cluster.Movies.First(m => m.Id.Equals(movieId));
        var source = analyzed.Sources[version.Id];
        var ownerHadOnlyThisVersion = analyzed.OwnerSourceCounts[owner.Id] == 1;

        var deleted = new List<string>();
        long freed = 0;
        try
        {
            if (version.DeletesFolder)
            {
                freed = new DirectoryInfo(version.DeleteTarget)
                    .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Sum(file => file.Length);
                Directory.Delete(version.DeleteTarget, recursive: true);
                deleted.Add(version.DeleteTarget);
            }
            else
            {
                freed = new FileInfo(source.Path).Length;
                File.Delete(source.Path);
                deleted.Add(source.Path);
                foreach (var sidecar in GetSidecars(source, analyzed.Sources.Values))
                {
                    try
                    {
                        freed += new FileInfo(sidecar).Length;
                        File.Delete(sidecar);
                        deleted.Add(sidecar);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Casifier could not delete sidecar {Sidecar}", sidecar);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Casifier could not delete {Target}", version.DeleteTarget);
            var partial = deleted.Count > 0 || version.DeletesFolder;
            return VersionDeleteOutcome.Failed(
                $"Could not delete {version.DeleteTarget}: {ex.Message}" + (partial ? " Some files may already be gone." : string.Empty));
        }

        _logger.LogInformation(
            "Casifier deleted the {Version} version of {MovieName}: {Files}",
            version.CaseLabel,
            owner.Name,
            string.Join(", ", deleted));

        if (ownerHadOnlyThisVersion)
        {
            // The whole Jellyfin item is gone from disk; drop it from the library now rather than
            // waiting for the next scan. Files are already deleted, and DeleteFileLocation = false
            // means Jellyfin itself touches nothing on disk.
            try
            {
                _libraryManager.DeleteItem(owner, new DeleteOptions { DeleteFileLocation = false });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Casifier could not remove {MovieName} from the library; the next scan will", owner.Name);
            }
        }

        try
        {
            _libraryMonitor.ReportFileSystemChanged(version.DeleteTarget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Casifier could not queue a library refresh for {Path}", version.DeleteTarget);
        }

        return VersionDeleteOutcome.Ok(new VersionDeleteResult
        {
            Message = $"Deleted the {version.CaseLabel} version of {owner.Name}.",
            DeletedFiles = deleted,
            FreedBytes = freed
        });
    }

    private List<MovieCluster> FindClusters()
    {
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = [BaseItemKind.Movie],
            MediaTypes = [MediaType.Video],
            SourceTypes = [SourceType.Library]
        }).OfType<Movie>()
            .Where(movie => string.IsNullOrEmpty(movie.PrimaryVersionId))
            .GroupBy(movie => movie.Id)
            .Select(group => group.First())
            .ToList();

        var candidates = movies.Select(movie => new MatchCandidate(
            movie.GetProviderId(MetadataProvider.Tmdb),
            movie.GetProviderId(MetadataProvider.Imdb),
            [movie.Name, movie.OriginalTitle],
            movie.ProductionYear,
            movie.LocalAlternateVersions.Length > 0 || movie.LinkedAlternateVersions.Length > 0)).ToList();

        return MovieMatcher.Cluster(candidates)
            .Select(cluster => new MovieCluster(cluster.Members.Select(i => movies[i]).ToList(), cluster))
            .ToList();
    }

    private AnalyzedCluster AnalyzeCluster(MovieCluster cluster, AnalysisContext context)
    {
        var owners = new Dictionary<string, Movie>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<MediaSourceInfo>();
        var seenPaths = new HashSet<string>(PathComparer);
        var unreachable = 0;
        var ownerSourceCounts = new Dictionary<Guid, int>();

        foreach (var movie in cluster.Movies)
        {
            // Counted before filtering, so a copy on an offline drive still counts as a copy.
            var movieSources = movie.GetMediaSources(false);
            ownerSourceCounts[movie.Id] = movieSources.Count;
            foreach (var source in movieSources)
            {
                if (string.IsNullOrEmpty(source.Id) || owners.ContainsKey(source.Id))
                {
                    continue;
                }

                if (source.Protocol == MediaProtocol.File && !File.Exists(source.Path) && !Directory.Exists(source.Path))
                {
                    unreachable++;
                    continue;
                }

                // Overlapping libraries can list the same file twice. It's one copy, not two.
                if (!string.IsNullOrEmpty(source.Path) && !seenPaths.Add(source.Path))
                {
                    continue;
                }

                owners[source.Id] = movie;
                sources.Add(source);
            }
        }

        if (sources.Count < 2)
        {
            return new AnalyzedCluster(null, new Dictionary<string, MediaSourceInfo>(), ownerSourceCounts, unreachable);
        }

        var versions = VersionAnalyzer.Analyze(sources, context.ResolveLanguage);
        var sourcesById = sources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions)
        {
            var owner = owners[version.Id];
            version.MovieId = owner.Id;
            if (!version.CanDelete)
            {
                continue;
            }

            PlanDeletion(version, owner, sourcesById[version.Id], ownerSourceCounts[owner.Id], sources, context.LibraryRoots);
        }

        var bestOwner = owners[versions[0].Id];
        return new AnalyzedCluster(
            new MovieVersionGroup
            {
                MovieId = bestOwner.Id,
                Name = bestOwner.Name ?? string.Empty,
                Year = bestOwner.ProductionYear,
                HasPrimaryImage = bestOwner.HasImage(ImageType.Primary),
                MatchedBy = cluster.Match.MatchedBy.ToList(),
                TitleMatchOnly = cluster.Match.TitleMatchOnly,
                Versions = versions
            },
            sourcesById,
            ownerSourceCounts,
            unreachable);
    }

    /// <summary>
    /// Decides what deleting a version removes. A version that is its movie item's only copy, alone
    /// in its own folder inside a library, takes the folder (artwork, NFO, subtitles) with it.
    /// Anything else removes just the video file and the external subtitle/audio files Jellyfin
    /// attributes to it. Jellyfin's own item delete is deliberately not used: for a movie in its
    /// own folder it deletes the folder even when other versions live there.
    /// </summary>
    private static void PlanDeletion(
        MovieVersion version,
        Movie owner,
        MediaSourceInfo source,
        int ownerVersionCount,
        List<MediaSourceInfo> allSources,
        IReadOnlyList<string> libraryRoots)
    {
        var path = source.Path;
        if (!File.Exists(path))
        {
            Block(version, "File not found on disk");
            return;
        }

        if (allSources.Any(s => !ReferenceEquals(s, source) && PathEquals(s.Path, path)))
        {
            Block(version, "Another version points at the same file");
            return;
        }

        version.DeleteTarget = path;
        version.DeletesFolder = false;

        var folder = Path.GetDirectoryName(path);
        if (ownerVersionCount != 1 || owner.IsInMixedFolder || string.IsNullOrEmpty(folder))
        {
            return;
        }

        var safeFolder = IsStrictlyInsideLibrary(folder, libraryRoots)
            && !libraryRoots.Any(root => IsSameOrInside(root, folder))
            && !allSources.Any(s => !ReferenceEquals(s, source) && IsSameOrInside(s.Path, folder))
            && FolderHoldsOnlyThisVideo(folder, path);
        if (safeFolder)
        {
            version.DeleteTarget = folder;
            version.DeletesFolder = true;
        }
    }

    private static void Block(MovieVersion version, string reason)
    {
        version.CanDelete = false;
        version.DeleteBlockedReason = reason;
        version.DeleteTarget = null;
        version.DeletesFolder = false;
    }

    private static bool FolderHoldsOnlyThisVideo(string folder, string videoPath)
    {
        try
        {
            var entries = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false }))
            {
                if (++entries > MaxFolderEntries)
                {
                    return false;
                }

                if (VideoExtensions.Contains(Path.GetExtension(entry)) && !PathEquals(entry, videoPath))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> GetSidecars(MediaSourceInfo source, IEnumerable<MediaSourceInfo> allSources)
    {
        var directory = Path.GetDirectoryName(source.Path);
        var kept = allSources
            .Where(s => !ReferenceEquals(s, source))
            .SelectMany(s => (s.MediaStreams ?? []).Select(stream => stream.Path).Append(s.Path))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(PathComparer);
        return (source.MediaStreams ?? [])
            .Where(stream => stream.IsExternal && !string.IsNullOrEmpty(stream.Path))
            .Select(stream => stream.Path)
            .Where(p => !kept.Contains(p)
                && !PathEquals(p, source.Path)
                && PathEquals(Path.GetDirectoryName(p), directory)
                && File.Exists(p))
            .Distinct(PathComparer)
            .ToList();
    }

    private List<string> GetLibraryRoots()
        => _libraryManager.GetVirtualFolders()
            .SelectMany(folder => folder.Locations ?? [])
            .Where(location => !string.IsNullOrWhiteSpace(location))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToList();

    private static bool IsStrictlyInsideLibrary(string folder, IReadOnlyList<string> roots)
        => roots.Any(root => !PathEquals(TrimSeparator(root), TrimSeparator(folder)) && IsSameOrInside(folder, root));

    private static bool IsSameOrInside(string? path, string folder)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var full = TrimSeparator(Path.GetFullPath(path));
        var parent = TrimSeparator(Path.GetFullPath(folder));
        return PathEquals(full, parent)
            || full.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison)
            || full.StartsWith(parent + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static string TrimSeparator(string path)
        => path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;

    private static bool PathEquals(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, PathComparison);

    private Func<string?, LanguageRef> CreateLanguageResolver()
    {
        var cache = new Dictionary<string, LanguageRef>(StringComparer.OrdinalIgnoreCase);
        return code =>
        {
            if (string.IsNullOrWhiteSpace(code) || UndeterminedLanguages.Contains(code.Trim()))
            {
                return LanguageRef.Unknown;
            }

            code = code.Trim();
            if (cache.TryGetValue(code, out var cached))
            {
                return cached;
            }

            var resolved = new LanguageRef(code.ToLowerInvariant(), code);
            try
            {
                // Collapses ISO 639-1/639-2 B/T variants (ja/jpn, zh/chi/zho, de/ger/deu) to one key.
                var culture = _localization.FindLanguageInfo(code);
                if (culture is not null)
                {
                    resolved = new LanguageRef(
                        (culture.ThreeLetterISOLanguageName ?? code).ToLowerInvariant(),
                        string.IsNullOrWhiteSpace(culture.DisplayName) ? code : culture.DisplayName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Casifier could not resolve language code {Code}", code);
            }

            cache[code] = resolved;
            return resolved;
        };
    }

    private sealed record MovieCluster(List<Movie> Movies, MatchCluster Match);

    private sealed record AnalysisContext(Func<string?, LanguageRef> ResolveLanguage, IReadOnlyList<string> LibraryRoots);

    private sealed record AnalyzedCluster(
        MovieVersionGroup? Group,
        IReadOnlyDictionary<string, MediaSourceInfo> Sources,
        IReadOnlyDictionary<Guid, int> OwnerSourceCounts,
        int Unreachable);
}

public sealed record VersionDeleteOutcome(int StatusCode, string Message, VersionDeleteResult? Result)
{
    public static VersionDeleteOutcome Ok(VersionDeleteResult result) => new(200, result.Message, result);

    public static VersionDeleteOutcome NotFound(string message) => new(404, message, null);

    public static VersionDeleteOutcome Conflict(string message) => new(409, message, null);

    public static VersionDeleteOutcome Failed(string message) => new(500, message, null);
}
