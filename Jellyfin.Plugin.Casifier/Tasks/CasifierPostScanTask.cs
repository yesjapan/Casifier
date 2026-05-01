using Jellyfin.Plugin.Casifier.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Casifier.Tasks;

public sealed class CasifierPostScanTask : ILibraryPostScanTask
{
    private readonly CasifierService _casifier;
    private readonly ILogger<CasifierPostScanTask> _logger;

    public CasifierPostScanTask(CasifierService casifier, ILogger<CasifierPostScanTask> logger)
    {
        _casifier = casifier;
        _logger = logger;
    }

    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.RunAfterLibraryScan != true)
        {
            return;
        }

        var result = await _casifier.ProcessMoviesAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Casifier post-scan finished. Processed: {Processed}, Updated: {Updated}, Skipped: {Skipped}, Failed: {Failed}", result.Processed, result.Updated, result.Skipped, result.Failed);
    }
}
