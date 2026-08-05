namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Result of a Wikipedia entity lookup.
/// </summary>
public class EntityLookupResult
{
    /// <summary>
    /// Gets or sets the looked-up title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the short extract/summary.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Wikipedia page URL.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets an optional thumbnail image URL (e.g. Wikipedia lead image).
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a match was found.
    /// </summary>
    public bool Found { get; set; }
}
