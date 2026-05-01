using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Casifier.Configuration;
using Jellyfin.Plugin.Casifier.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.Casifier.Services;

public sealed class CasifierService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CasifierService> _logger;

    public CasifierService(ILibraryManager libraryManager, ILogger<CasifierService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task<CasifierResult> ProcessMoviesAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.OverwritePrimaryPoster)
        {
            return new CasifierResult(0, 0, 0, 0);
        }

        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = [BaseItemKind.Movie],
            MediaTypes = [MediaType.Video],
            SourceTypes = [SourceType.Library]
        }).OfType<Movie>().ToList();

        var updated = 0;
        var skipped = 0;
        var failed = 0;

        for (var index = 0; index < movies.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(movies.Count == 0 ? 100 : index * 100d / movies.Count);

            try
            {
                if (await ProcessMovieAsync(movies[index], config, cancellationToken).ConfigureAwait(false))
                {
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to casify poster for {MovieName}", movies[index].Name);
            }
        }

        progress.Report(100);
        return new CasifierResult(movies.Count, updated, skipped, failed);
    }

    private static async Task<bool> ProcessMovieAsync(Movie movie, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var image = movie.GetImageInfo(ImageType.Primary, 0);
        if (image is null || string.IsNullOrWhiteSpace(image.Path) || !File.Exists(image.Path))
        {
            return false;
        }

        var height = GetHighestVideoHeight(movie);
        if (height is null)
        {
            return false;
        }

        var caseKind = ResolveCaseKind(height.Value);
        var sourcePath = EnsureBackup(image.Path, config.BackupSuffix);
        await RenderCaseAsync(sourcePath, image.Path, caseKind, cancellationToken).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(image.Path, DateTime.UtcNow);
        return true;
    }

    private static string EnsureBackup(string posterPath, string backupSuffix)
    {
        var directory = Path.GetDirectoryName(posterPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(posterPath);
        var extension = Path.GetExtension(posterPath);
        var backupPath = Path.Combine(directory, $"{fileName}{backupSuffix}{extension}");

        if (!File.Exists(backupPath))
        {
            File.Copy(posterPath, backupPath);
        }

        return backupPath;
    }

    private static CaseKind ResolveCaseKind(int height)
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

    private static int? GetHighestVideoHeight(Movie movie)
    {
        var heights = new List<int>();

        foreach (var source in movie.GetMediaSources(false))
        {
            heights.AddRange(source.MediaStreams
                .Where(stream => stream.Type == MediaStreamType.Video && stream.Height.HasValue)
                .Select(stream => stream.Height!.Value));

            if (TryInferHeight(source.Name, out var sourceNameHeight))
            {
                heights.Add(sourceNameHeight);
            }

            if (TryInferHeight(source.Path, out var sourcePathHeight))
            {
                heights.Add(sourcePathHeight);
            }

            foreach (var stream in source.MediaStreams.Where(stream => stream.Type == MediaStreamType.Video))
            {
                if (TryInferHeight(stream.DisplayTitle, out var displayHeight))
                {
                    heights.Add(displayHeight);
                }

                if (TryInferHeight(stream.Codec, out var codecHeight))
                {
                    heights.Add(codecHeight);
                }
            }
        }

        var defaultStream = movie.GetDefaultVideoStream();
        if (defaultStream?.Height is not null)
        {
            heights.Add(defaultStream.Height.Value);
        }

        return heights.Count == 0 ? null : heights.Max();
    }

    private static bool TryInferHeight(string? value, out int height)
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

    private static async Task RenderCaseAsync(string sourcePath, string outputPath, CaseKind caseKind, CancellationToken cancellationToken)
    {
        using var poster = await Image.LoadAsync<Rgba32>(sourcePath, cancellationToken).ConfigureAwait(false);

        const int targetHeight = 1500;
        var posterWidth = (int)Math.Round(poster.Width * (targetHeight / (double)poster.Height));
        poster.Mutate(x => x.Resize(posterWidth, targetHeight));

        var topBandHeight = Math.Max(92, targetHeight / 14);
        var canvasWidth = posterWidth;
        var canvasHeight = targetHeight;

        using var canvas = new Image<Rgba32>(canvasWidth, canvasHeight, Color.Black);
        var palette = GetPalette(caseKind);
        var label = GetLabel(caseKind);

        canvas.Mutate(ctx =>
        {
            var posterRect = new Rectangle(0, 0, posterWidth, targetHeight);
            var bandRect = new Rectangle(0, 0, posterWidth, topBandHeight);
            var wholeRect = new Rectangle(0, 0, posterWidth, targetHeight);

            ctx.DrawImage(poster, posterRect.Location, 1f);
            ctx.Fill(palette.Band.WithAlpha(0.94f), bandRect);

            DrawHighlights(ctx, wholeRect, bandRect);
            DrawLabel(ctx, label, palette.Text, bandRect.X, bandRect.Y, bandRect.Width, bandRect.Height);
        });

        await canvas.SaveAsJpegAsync(outputPath, new JpegEncoder { Quality = 92 }, cancellationToken).ConfigureAwait(false);
    }

    private static (Color Band, Color Spine, Color Text) GetPalette(CaseKind caseKind)
        => caseKind switch
        {
            CaseKind.UltraHd => (Color.ParseHex("171717"), Color.ParseHex("060606"), Color.ParseHex("f6d451")),
            CaseKind.Bluray => (Color.ParseHex("1658ba"), Color.ParseHex("0b3477"), Color.White),
            _ => (Color.ParseHex("f2f2f2"), Color.ParseHex("d8d8d8"), Color.ParseHex("202020"))
        };

    private static string GetLabel(CaseKind caseKind)
        => caseKind switch
        {
            CaseKind.UltraHd => "ULTRA HD",
            CaseKind.Bluray => "BLU-RAY",
            _ => "DVD"
        };

    private static void DrawHighlights(IImageProcessingContext ctx, Rectangle wholeRect, Rectangle bandRect)
    {
        ctx.Draw(Color.ParseHex("000000"), 2, wholeRect);
        ctx.Fill(Color.ParseHex("ffffff").WithAlpha(0.14f), new Rectangle(bandRect.X, bandRect.Y + bandRect.Height - 4, bandRect.Width, 4));
        ctx.Fill(Color.ParseHex("000000").WithAlpha(0.28f), new Rectangle(bandRect.X, bandRect.Y + bandRect.Height, bandRect.Width, 8));
    }

    private static void DrawLabel(IImageProcessingContext ctx, string label, Color color, int x, int y, int width, int height)
    {
        var font = ResolveFont(Math.Max(34, height / 2));
        var textOptions = new RichTextOptions(font)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Origin = new PointF(x + width / 2f, y + height / 2f)
        };

        ctx.DrawText(textOptions, label, color);
    }

    private static Font ResolveFont(float size)
    {
        if (SystemFonts.TryGet("Arial", out var arial))
        {
            return arial.CreateFont(size, FontStyle.Bold);
        }

        return SystemFonts.Collection.Families.First().CreateFont(size, FontStyle.Bold);
    }
}
