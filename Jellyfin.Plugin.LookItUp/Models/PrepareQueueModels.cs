namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Persisted overnight prepare queue (pending + failed retries).
/// </summary>
public sealed class PrepareQueueState
{
    /// <summary>Gets or sets item ids waiting to be prepared.</summary>
    public List<Guid> Pending { get; set; } = [];

    /// <summary>Gets or sets failed items awaiting retry.</summary>
    public List<PrepareQueueFailure> Failed { get; set; } = [];

    /// <summary>Gets or sets when the queue was last updated (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A failed prepare entry with backoff metadata.
/// </summary>
public sealed class PrepareQueueFailure
{
    /// <summary>Gets or sets the media item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets how many attempts have been made.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets the last error message.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets the earliest UTC time this item may be retried.</summary>
    public DateTime NextRetryUtc { get; set; }

    /// <summary>Gets or sets a display name for status UI.</summary>
    public string? ItemName { get; set; }
}
