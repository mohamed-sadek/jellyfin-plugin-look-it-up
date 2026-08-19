namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Cached scan result for a media item.
/// </summary>
public class ItemAnnotationCache
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the scan logic version used to produce this cache entry.</summary>
    public int Version { get; set; }

    /// <summary>Gets or sets when the cache entry was created (UTC).</summary>
    public DateTime ScannedAtUtc { get; set; }

    /// <summary>Gets or sets the path/label of the subtitle that was scanned.</summary>
    public string? SubtitlePath { get; set; }

    /// <summary>
    /// Gets or sets subtitle source: <c>external</c>, <c>embedded</c>, <c>opensubtitles</c>.
    /// </summary>
    public string? SubtitleSource { get; set; }

    /// <summary>Gets or sets content hash of the subtitle text (for identity).</summary>
    public string? SubtitleHash { get; set; }

    /// <summary>Gets or sets OpenSubtitles moviehash of the video file when used.</summary>
    public string? MovieHash { get; set; }

    /// <summary>
    /// Gets or sets how the subtitle was matched:
    /// <c>stem</c>, <c>moviehash</c>, <c>metadata</c>, <c>embedded</c>, <c>folder</c>.
    /// </summary>
    public string? MatchedBy { get; set; }

    /// <summary>Gets or sets whether subtitle timing passed the duration sanity check.</summary>
    public bool DurationCheckOk { get; set; } = true;

    /// <summary>
    /// Gets or sets prepare outcome:
    /// <c>success</c>, <c>no-candidates</c>, <c>no-subtitles</c>, <c>failed</c>.
    /// </summary>
    public string? PrepareOutcome { get; set; }

    /// <summary>Gets or sets series name for audits (TV).</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets season number for audits (TV).</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets episode number for audits (TV).</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether popups are disabled for this item
    /// (annotations are kept).
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>Gets or sets the annotations for this item.</summary>
    public List<ContextAnnotation> Annotations { get; set; } = [];

    /// <summary>
    /// Gets or sets how far (ms) incremental prepare has scanned ahead in the timeline.
    /// </summary>
    public long PreparedThroughMs { get; set; }

    /// <summary>
    /// Gets or sets whether the full subtitle timeline has been incrementally prepared.
    /// </summary>
    public bool FullyPrepared { get; set; }
}
