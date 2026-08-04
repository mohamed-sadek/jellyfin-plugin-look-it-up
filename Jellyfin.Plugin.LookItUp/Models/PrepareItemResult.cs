namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Result of preparing annotations for a single media item.
/// </summary>
public sealed class PrepareItemResult
{
    /// <summary>Gets or sets the saved cache entry (null when prepare did not run).</summary>
    public ItemAnnotationCache? Cache { get; init; }

    /// <summary>Gets or sets which extractor was used (<c>ai</c> or <c>legacy</c>).</summary>
    public string Mode { get; init; } = "none";

    /// <summary>Gets or sets the resolved AI chat-completions base URL when mode is AI.</summary>
    public string? AiBaseUrl { get; init; }

    /// <summary>Gets or sets the AI model id when mode is AI.</summary>
    public string? AiModel { get; init; }

    /// <summary>Gets or sets a warning when AI failed or returned no mentions.</summary>
    public string? Warning { get; init; }
}
