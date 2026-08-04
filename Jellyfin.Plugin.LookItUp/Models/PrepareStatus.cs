namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Progress for a library prepare/scan job.
/// </summary>
public class PrepareStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether a prepare job is running.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets how many items are in the current job.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Gets or sets how many items have been processed.
    /// </summary>
    public int Completed { get; set; }

    /// <summary>
    /// Gets or sets how many items produced annotations.
    /// </summary>
    public int WithAnnotations { get; set; }

    /// <summary>
    /// Gets or sets how many items were skipped (no subs / already prepared).
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Gets or sets how many items failed.
    /// </summary>
    public int Failed { get; set; }

    /// <summary>
    /// Gets or sets the item currently being prepared.
    /// </summary>
    public string? CurrentItem { get; set; }

    /// <summary>
    /// Gets or sets the last error message, if any.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets when the job started (UTC).
    /// </summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets when the job finished (UTC).
    /// </summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>
    /// Gets percent complete (0–100).
    /// </summary>
    public double Percent => Total <= 0 ? 0 : Math.Round(100.0 * Completed / Total, 1);
}
