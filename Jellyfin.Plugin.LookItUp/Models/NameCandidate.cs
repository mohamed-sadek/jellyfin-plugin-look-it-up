namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// A name/phrase candidate extracted from subtitles before AI verification.
/// </summary>
public sealed class NameCandidate
{
    /// <summary>Gets or sets the candidate surface form.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Gets or sets first appearance start time (ms).</summary>
    public long StartMs { get; set; }

    /// <summary>Gets or sets first appearance end time (ms).</summary>
    public long EndMs { get; set; }

    /// <summary>Gets or sets the subtitle line where it first appeared.</summary>
    public string CueText { get; set; } = string.Empty;

    /// <summary>Gets or sets ranking score (higher = likelier name).</summary>
    public int Score { get; set; }

    /// <summary>Gets or sets why it was kept (for dry-run debugging).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets mid-sentence capitalized hit count for the head token.</summary>
    public int MidSentenceHits { get; set; }
}

/// <summary>
/// Dry-run result of local name finding (no AI).
/// </summary>
public sealed class NameCandidatesResult
{
    /// <summary>Gets or sets the media item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the media item name.</summary>
    public string? ItemName { get; set; }

    /// <summary>Gets or sets the subtitle source label.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets how many subtitle cues were scanned.</summary>
    public int CueCount { get; set; }

    /// <summary>Gets or sets ranked candidates that would be sent to AI.</summary>
    public IReadOnlyList<NameCandidate> Candidates { get; set; } = [];

    /// <summary>Gets or sets cast/character names excluded via Jellyfin metadata.</summary>
    public IReadOnlyList<string> ExcludedCastNames { get; set; } = [];

    /// <summary>Gets or sets an optional warning.</summary>
    public string? Warning { get; set; }
}
