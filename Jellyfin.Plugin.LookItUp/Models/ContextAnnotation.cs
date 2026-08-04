namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// A timed context annotation shown during playback.
/// </summary>
public class ContextAnnotation
{
    /// <summary>
    /// Gets or sets the entity name (e.g. "France").
    /// </summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a short explanation of the term.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional URL for more information.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets the playback time (ms) when the popup should appear.
    /// </summary>
    public long StartMs { get; set; }

    /// <summary>
    /// Gets or sets the playback time (ms) when the popup should hide.
    /// </summary>
    public long EndMs { get; set; }
}
