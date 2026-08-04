namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// A named entity / cultural reference extracted by AI from subtitle context.
/// </summary>
public class AiEntityMention
{
    /// <summary>
    /// Gets or sets the canonical display name (e.g. "Jon Voight").
    /// </summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the entity kind (person, place, film, org, other).
    /// </summary>
    public string Kind { get; set; } = "other";

    /// <summary>
    /// Gets or sets a short viewer-facing explanation in context.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the mention occurs (ms).
    /// </summary>
    public long StartMs { get; set; }

    /// <summary>
    /// Gets or sets when the popup window should end (ms).
    /// </summary>
    public long EndMs { get; set; }
}
