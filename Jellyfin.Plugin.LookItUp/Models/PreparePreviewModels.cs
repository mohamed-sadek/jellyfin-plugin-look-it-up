namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Preview of name candidates that can be sent to AI for one or more items.
/// </summary>
public sealed class PreparePreviewResult
{
    /// <summary>Gets or sets the root item id (series, season, episode, or movie).</summary>
    public Guid RootItemId { get; set; }

    /// <summary>Gets or sets the root item name.</summary>
    public string? RootItemName { get; set; }

    /// <summary>Gets or sets the root item type name.</summary>
    public string? RootItemType { get; set; }

    /// <summary>Gets or sets the default AI names-per-item from config.</summary>
    public int DefaultNamesPerPrepare { get; set; }

    /// <summary>Gets or sets the names-per-item used to mark suggestions in this preview.</summary>
    public int SuggestedNamesPerItem { get; set; }

    /// <summary>Gets or sets per-episode/movie candidate groups.</summary>
    public IReadOnlyList<PreparePreviewItem> Items { get; set; } = [];

    /// <summary>Gets or sets an optional warning.</summary>
    public string? Warning { get; set; }
}

/// <summary>
/// Candidates for a single playable item.
/// </summary>
public sealed class PreparePreviewItem
{
    /// <summary>Gets or sets the media item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets season number when known.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets episode number when known.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the subtitle source label.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets how many subtitle cues were scanned.</summary>
    public int CueCount { get; set; }

    /// <summary>Gets or sets whether this item already has a current prepare cache.</summary>
    public bool AlreadyPrepared { get; set; }

    /// <summary>Gets or sets ranked candidates.</summary>
    public IReadOnlyList<PreparePreviewCandidate> Candidates { get; set; } = [];

    /// <summary>Gets or sets an optional warning for this item.</summary>
    public string? Warning { get; set; }
}

/// <summary>
/// A single name candidate in a prepare preview.
/// </summary>
public sealed class PreparePreviewCandidate
{
    /// <summary>Gets or sets the candidate term.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Gets or sets ranking score.</summary>
    public int Score { get; set; }

    /// <summary>Gets or sets why it was kept.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets first appearance start (ms).</summary>
    public long StartMs { get; set; }

    /// <summary>Gets or sets first appearance end (ms).</summary>
    public long EndMs { get; set; }

    /// <summary>Gets or sets the subtitle line.</summary>
    public string CueText { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this would be in the default top-N AI batch.</summary>
    public bool Suggested { get; set; }
}

/// <summary>
/// Request body for preparing only user-selected terms.
/// </summary>
public sealed class PrepareSelectedRequest
{
    /// <summary>Gets or sets whether to overwrite existing caches.</summary>
    public bool Force { get; set; } = true;

    /// <summary>Gets or sets per-item term selections.</summary>
    public IReadOnlyList<PrepareSelectedItem> Items { get; set; } = [];
}

/// <summary>
/// Selected terms for one media item.
/// </summary>
public sealed class PrepareSelectedItem
{
    /// <summary>Gets or sets the media item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets terms to verify with AI.</summary>
    public IReadOnlyList<string> Terms { get; set; } = [];
}
