namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// Wikipedia + Wikidata resolution for one subtitle name candidate.
/// </summary>
public sealed class WikimediaReferenceHit
{
    /// <summary>Gets or sets the original candidate term.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Gets or sets the Wikipedia article title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a short Wikipedia extract.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets the Wikipedia page URL.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets an optional lead image URL.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Gets or sets the Wikidata Q-id when known.</summary>
    public string? WikidataId { get; set; }

    /// <summary>Gets or sets Wikidata P31 instance-of Q-ids.</summary>
    public IReadOnlyList<string> InstanceOfIds { get; set; } = [];

    /// <summary>Gets or sets the English Wikidata description.</summary>
    public string? WikidataDescription { get; set; }

    /// <summary>Gets or sets whether a standard article was found.</summary>
    public bool Found { get; set; }

    /// <summary>Gets or sets true when the top search hits scored closely.</summary>
    public bool Ambiguous { get; set; }

    /// <summary>Gets or sets runner-up Wikipedia titles when the pick was close.</summary>
    public IReadOnlyList<string> AlternateTitles { get; set; } = [];
}
