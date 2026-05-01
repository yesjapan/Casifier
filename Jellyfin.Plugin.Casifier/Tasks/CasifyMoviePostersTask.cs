using Jellyfin.Plugin.Casifier.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Casifier.Tasks;

public sealed class CasifyMoviePostersTask : IScheduledTask
{
    private readonly CasifierService _casifier;
    private readonly ILogger<CasifyMoviePostersTask> _logger;

    public CasifyMoviePostersTask(CasifierService casifier, ILogger<CasifyMoviePostersTask> logger)
    {
        _casifier = casifier;
        _logger = logger;
    }

    public string Name => "Casify Movie Posters";

    public string Key => "CasifierMoviePosters";

    public string Description => "Creates DVD, Blu-ray, and Ultra HD case-style movie posters based on video resolution.";

    public string Category => "Library";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = await _casifier.ProcessMoviesAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Casifier finished. Processed: {Processed}, Updated: {Updated}, Skipped: {Skipped}, Failed: {Failed}", result.Processed, result.Updated, result.Skipped, result.Failed);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }
}
