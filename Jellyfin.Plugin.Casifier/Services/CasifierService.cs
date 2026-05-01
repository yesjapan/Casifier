using Jellyfin.Data.Enums;
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
                if (await ProcessMovieAsync(movies[index], config.BackupSuffix, cancellationToken).ConfigureAwait(false))
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

    private static async Task<bool> ProcessMovieAsync(Movie movie, string backupSuffix, CancellationToken cancellationToken)
    {
        var image = movie.GetImageInfo(ImageType.Primary, 0);
        if (image is null || string.IsNullOrWhiteSpace(image.Path) || !File.Exists(image.Path))
        {
            return false;
        }

        var stream = movie.GetDefaultVideoStream();
        if (stream?.Height is null)
        {
            return false;
        }

        var caseKind = ResolveCaseKind(stream.Height.Value);
        var sourcePath = EnsureBackup(image.Path, backupSuffix);
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

    private static async Task RenderCaseAsync(string sourcePath, string outputPath, CaseKind caseKind, CancellationToken cancellationToken)
    {
        using var poster = await Image.LoadAsync<Rgba32>(sourcePath, cancellationToken).ConfigureAwait(false);

        const int targetHeight = 1500;
        var posterWidth = (int)Math.Round(poster.Width * (targetHeight / (double)poster.Height));
        poster.Mutate(x => x.Resize(posterWidth, targetHeight));

        var spineWidth = Math.Max(68, posterWidth / 12);
        var topBandHeight = Math.Max(92, targetHeight / 14);
        var padding = Math.Max(24, posterWidth / 32);
        var canvasWidth = posterWidth + spineWidth + padding * 2;
        var canvasHeight = targetHeight + topBandHeight + padding * 2;

        using var canvas = new Image<Rgba32>(canvasWidth, canvasHeight, Color.Transparent);
        var palette = GetPalette(caseKind);
        var label = GetLabel(caseKind);

        canvas.Mutate(ctx =>
        {
            var caseRect = new Rectangle(padding, padding, posterWidth + spineWidth, targetHeight + topBandHeight);
            ctx.Fill(Color.ParseHex("111111"), caseRect);
            ctx.Fill(palette.Spine, new Rectangle(padding, padding, spineWidth, caseRect.Height));
            ctx.Fill(palette.Band, new Rectangle(padding + spineWidth, padding, posterWidth, topBandHeight));
            ctx.DrawImage(poster, new Point(padding + spineWidth, padding + topBandHeight), 1f);

            DrawHighlights(ctx, caseRect, spineWidth, padding);
            DrawLabel(ctx, label, palette.Text, padding + spineWidth, padding, posterWidth, topBandHeight);
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

    private static void DrawHighlights(IImageProcessingContext ctx, Rectangle caseRect, int spineWidth, int padding)
    {
        ctx.Draw(Color.ParseHex("000000"), 3, caseRect);
        ctx.Draw(Color.ParseHex("ffffff").WithAlpha(0.35f), 2, new Rectangle(caseRect.X + 5, caseRect.Y + 5, caseRect.Width - 10, caseRect.Height - 10));
        ctx.Fill(Color.ParseHex("ffffff").WithAlpha(0.14f), new Rectangle(padding + spineWidth - 4, padding, 4, caseRect.Height));
        ctx.Fill(Color.ParseHex("000000").WithAlpha(0.35f), new Rectangle(padding + spineWidth, padding, 8, caseRect.Height));
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
