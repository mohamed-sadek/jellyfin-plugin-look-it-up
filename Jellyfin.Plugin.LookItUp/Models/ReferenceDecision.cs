namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Keep/drop outcome of the Wikimedia reference gate for one candidate.
/// </summary>
public sealed class ReferenceDecision
{
    /// <summary>Gets or sets the candidate that was evaluated.</summary>
    public NameCandidate Candidate { get; set; } = new();

    /// <summary>Gets or sets whether this should become a popup.</summary>
    public bool Kept { get; set; }

    /// <summary>Gets or sets a short category tag (person, in-show, too-common, …).</summary>
    public string Category { get; set; } = "no-value";

    /// <summary>Gets or sets why it was kept or dropped.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets popup kind when kept (person, place, film, brand, other).</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the canonical title when resolved.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the viewer-facing summary when kept.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets the Wikipedia URL when kept.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets an optional image URL when kept.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Gets or sets the Wikidata Q-id when resolved.</summary>
    public string? WikidataId { get; set; }

    /// <summary>
    /// Gets or sets whether Groq should break a tie or salvage a weak drop.
    /// Local in-show / calendar / geography drops stay final.
    /// </summary>
    public bool Uncertain { get; set; }

    /// <summary>Gets or sets runner-up Wikipedia titles for a Groq tie-break.</summary>
    public IReadOnlyList<string> AlternateTitles { get; set; } = [];
}
