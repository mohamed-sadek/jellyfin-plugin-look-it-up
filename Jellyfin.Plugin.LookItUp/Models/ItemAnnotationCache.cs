using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Cached scan result for a media item.
/// </summary>
public class ItemAnnotationCache
{
    /// <summary>
    /// Gets or sets the Jellyfin item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the scan logic version used to produce this cache entry.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Gets or sets when the cache entry was created (UTC).
    /// </summary>
    public DateTime ScannedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the path of the subtitle file that was scanned.
    /// </summary>
    public string? SubtitlePath { get; set; }

    /// <summary>
    /// Gets or sets the annotations for this item.
    /// </summary>
    public List<ContextAnnotation> Annotations { get; set; } = [];
}
