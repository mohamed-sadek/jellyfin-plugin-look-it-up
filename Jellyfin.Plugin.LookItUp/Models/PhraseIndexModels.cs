namespace Jellyfin.Plugin.LookItUp.Models;

/// <summary>
/// One searchable phrase mapped to an English Wikipedia title.
/// </summary>
public sealed class PhraseIndexEntry
{
    /// <summary>Gets or sets the lowercase phrase matched in subtitles.</summary>
    public string Phrase { get; set; } = string.Empty;

    /// <summary>Gets or sets the English Wikipedia article title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the Wikidata Q-id when known.</summary>
    public string? Qid { get; set; }

    /// <summary>Gets or sets popup kind (person, brand, film, place, other).</summary>
    public string Kind { get; set; } = "other";
}

/// <summary>
/// On-disk / embedded phrase index payload.
/// </summary>
public sealed class PhraseIndexFile
{
    /// <summary>Gets or sets schema version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets when the index was generated (UTC).</summary>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>Gets or sets phrase entries.</summary>
    public List<PhraseIndexEntry> Entries { get; set; } = [];
}

/// <summary>
/// A timed phrase hit inside a subtitle cue.
/// </summary>
public sealed class PhraseMatch
{
    /// <summary>Gets or sets the matched surface phrase.</summary>
    public string Phrase { get; set; } = string.Empty;

    /// <summary>Gets or sets the Wikipedia title to fetch.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the Wikidata Q-id when known.</summary>
    public string? Qid { get; set; }

    /// <summary>Gets or sets popup kind.</summary>
    public string Kind { get; set; } = "other";

    /// <summary>Gets or sets cue start (ms).</summary>
    public long StartMs { get; set; }

    /// <summary>Gets or sets cue end (ms).</summary>
    public long EndMs { get; set; }

    /// <summary>Gets or sets the cue text.</summary>
    public string CueText { get; set; } = string.Empty;
}

/// <summary>
/// Raw Wikidata entity used while compiling the phrase index.
/// </summary>
public sealed class PhraseIndexSourceEntity
{
    /// <summary>Gets or sets Wikidata Q-id.</summary>
    public string Qid { get; set; } = string.Empty;

    /// <summary>Gets or sets English Wikipedia title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets sitelink count (language editions).</summary>
    public int Sitelinks { get; set; }

    /// <summary>Gets or sets P31 instance-of Q-ids.</summary>
    public List<string> Types { get; set; } = [];

    /// <summary>Gets or sets English labels, aliases, and the Wikipedia title.</summary>
    public List<string> Phrases { get; set; } = [];
}
