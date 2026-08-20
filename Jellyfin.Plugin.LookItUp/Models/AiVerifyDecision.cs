namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// One AI keep/reject decision for debugging and tuning.
/// </summary>
public sealed class AiVerifyDecision
{
    /// <summary>Gets or sets the candidate term sent to AI.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Gets or sets cue start time (ms).</summary>
    public long StartMs { get; set; }

    /// <summary>Gets or sets the subtitle line context.</summary>
    public string? CueText { get; set; }

    /// <summary>Gets or sets whether the candidate was kept for popups.</summary>
    public bool Kept { get; set; }

    /// <summary>
    /// Gets or sets AI or local filter explanation.
    /// Reject: why no popup. Keep: why it merits a popup.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets a short category tag, e.g. in-show, too-common, person-reference, ordinary-prop.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets when the decision was recorded (UTC).</summary>
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}
