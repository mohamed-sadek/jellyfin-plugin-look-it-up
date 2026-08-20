using Jellyfin.Plugin.LookItUp.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.ScheduledTasks;

/// <summary>
/// Scheduled / dashboard task that precomputes Look it up annotations for the library.
/// </summary>
public class LookItUpPrepareTask : IScheduledTask
{
    private readonly ILogger<LookItUpPrepareTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookItUpPrepareTask"/> class.
    /// </summary>
    public LookItUpPrepareTask(ILogger<LookItUpPrepareTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Look it up — prepare library";

    /// <inheritdoc />
    public string Key => "LookItUpPrepareLibrary";

    /// <inheritdoc />
    public string Description =>
        "Disabled — Look it up uses incremental prepare during playback instead of overnight library scans.";

    /// <inheritdoc />
    public string Category => "Look it up";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Scheduled Look it up library prepare is disabled (incremental playback prepare is used instead)");
        progress.Report(100);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => [];
}
