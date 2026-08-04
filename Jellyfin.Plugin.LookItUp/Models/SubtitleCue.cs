namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// A single timed subtitle cue.
/// </summary>
public class SubtitleCue
{
    /// <summary>
    /// Gets or sets the cue start time in milliseconds.
    /// </summary>
    public long StartMs { get; set; }

    /// <summary>
    /// Gets or sets the cue end time in milliseconds.
    /// </summary>
    public long EndMs { get; set; }

    /// <summary>
    /// Gets or sets the plain text of the cue.
    /// </summary>
    public string Text { get; set; } = string.Empty;
}
