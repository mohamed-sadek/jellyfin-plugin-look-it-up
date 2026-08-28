using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Scans subtitle cues against the phrase index (longest match wins).
/// </summary>
public interface IPhraseIndexScanner
{
    /// <summary>Finds phrase matches in the given cues.</summary>
    IReadOnlyList<PhraseMatch> Find(
        IReadOnlyList<SubtitleCue> cues,
        int minLength,
        int maxMatches);

    /// <summary>True when the index loaded at least one phrase.</summary>
    bool HasIndex { get; }
}

/// <summary>
/// Aho-Corasick scan of each cue; overlapping hits keep the longest phrase.
/// </summary>
public sealed class PhraseIndexScanner : IPhraseIndexScanner
{
    private readonly IReadOnlyList<PhraseIndexEntry> _entries;
    private readonly AhoCorasickMatcher _matcher;

    /// <summary>
    /// Initializes a new instance of the <see cref="PhraseIndexScanner"/> class.
    /// </summary>
    public PhraseIndexScanner(IPhraseIndexStore store)
    {
        _entries = store.Entries;
        _matcher = new AhoCorasickMatcher(_entries.Select(e => e.Phrase));
        HasIndex = _entries.Count > 0;
    }

    /// <inheritdoc />
    public bool HasIndex { get; }

    /// <inheritdoc />
    public IReadOnlyList<PhraseMatch> Find(
        IReadOnlyList<SubtitleCue> cues,
        int minLength,
        int maxMatches)
    {
        if (!HasIndex || cues.Count == 0 || maxMatches <= 0)
        {
            return [];
        }

        var min = Math.Max(2, minLength);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<PhraseMatch>();
        foreach (var cue in cues.OrderBy(c => c.StartMs))
        {
            if (string.IsNullOrWhiteSpace(cue.Text))
            {
                continue;
            }

            var normalized = AhoCorasickMatcher.Normalize(cue.Text);
            foreach (var hit in _matcher.Find(normalized))
            {
                var entry = _entries[hit.PhraseIndex];
                if (entry.Phrase.Length < min)
                {
                    continue;
                }

                if (!seen.Add(entry.Title))
                {
                    continue;
                }

                matches.Add(new PhraseMatch
                {
                    Phrase = cue.Text.Substring(hit.Start, hit.Length),
                    Title = entry.Title,
                    Qid = entry.Qid,
                    Kind = entry.Kind,
                    StartMs = cue.StartMs,
                    EndMs = cue.EndMs,
                    CueText = cue.Text
                });

                if (matches.Count >= maxMatches)
                {
                    return matches;
                }
            }
        }

        return matches;
    }
}
