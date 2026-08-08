using Jellyfin.Plugin.LookItUp.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.ScheduledTasks;

/// <summary>
/// Scheduled / dashboard task that precomputes Look it up annotations for the library.
/// </summary>
public class LookItUpPrepareTask : IScheduledTask
{
    private readonly ILookItUpPrepareService _prepareService;
    private readonly ILogger<LookItUpPrepareTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookItUpPrepareTask"/> class.
    /// </summary>
    public LookItUpPrepareTask(
        ILookItUpPrepareService prepareService,
        ILogger<LookItUpPrepareTask> logger)
    {
        _prepareService = prepareService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Look it up — prepare library";

    /// <inheritdoc />
    public string Key => "LookItUpPrepareLibrary";

    /// <inheritdoc />
    public string Description =>
        "Prepares Look it up annotations overnight: finds/downloads subtitles, verifies names with AI under rate limits, retries failures.";

    /// <inheritdoc />
    public string Category => "Look it up";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Scheduled Look it up library prepare starting");
        await _prepareService
            .RunLibraryPrepareAsync(force: false, progress, cancellationToken)
            .ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily at 02:00 local — editable in Dashboard → Scheduled Tasks.
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            }
        ];
    }
}
