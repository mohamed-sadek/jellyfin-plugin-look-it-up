using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Extracts candidate named entities from subtitle text.
/// </summary>
public interface IEntityExtractor
{
    /// <summary>
    /// Extracts candidate entity names from a subtitle cue.
    /// </summary>
    /// <param name="text">Cue text.</param>
    /// <param name="minLength">Minimum entity length.</param>
    /// <returns>Candidate names in appearance order.</returns>
    IReadOnlyList<string> Extract(string text, int minLength);
}

/// <summary>
/// Finds likely proper nouns in English subtitle cues.
/// <para>
/// Strategy (v1):
/// 1. Find capitalized word sequences ("France", "New York").
/// 2. Drop dialogue/grammar noise via stop words and sentence-start filtering.
/// 3. Let Wikipedia confirmation (in <see cref="LookItUpService"/>) decide what is real.
/// </para>
/// This is intentionally not full NLP — subtitles are short, timed lines, and a
/// Wikipedia hit is a strong signal that a candidate is a real named entity.
/// </summary>
public partial class EntityExtractor : IEntityExtractor
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "A", "An", "The", "This", "That", "These", "Those", "There", "Here",
        "I", "You", "He", "She", "We", "They", "It", "My", "Your", "His", "Her", "Our", "Their",
        "And", "Or", "But", "If", "Then", "So", "Because", "As", "Of", "In", "On", "At", "To", "For",
        "From", "With", "About", "Into", "Over", "After", "Before", "Between", "Under", "Again",
        "Yes", "No", "Okay", "Ok", "Hey", "Hi", "Hello", "Thanks", "Please", "Sorry", "Well",
        "What", "When", "Where", "Who", "Why", "How", "Which", "Whom", "Whose",
        "Is", "Are", "Was", "Were", "Be", "Been", "Being", "Have", "Has", "Had", "Do", "Does", "Did",
        "Will", "Would", "Could", "Should", "May", "Might", "Must", "Can", "Shall",
        "Not", "Don", "Didn", "Isn", "Aren", "Wasn", "Weren", "Hasn", "Haven", "Hadn",
        "Mr", "Mrs", "Ms", "Dr", "Sir", "Madam", "Captain", "Episode", "Season",
        "Oh", "Ah", "Uh", "Um", "Right", "Sure", "Maybe", "Really", "Actually", "Anyway",
        "Today", "Tomorrow", "Yesterday", "Tonight", "Morning", "Night", "God", "Hell", "Damn"
    };

    /// <inheritdoc />
    public IReadOnlyList<string> Extract(string text, int minLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // Normalize ellipses / odd spacing so "France..." still matches cleanly.
        var normalized = EllipsisRegex().Replace(text, " ");
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ProperNounRegex().Matches(normalized))
        {
            var candidate = match.Value.Trim().TrimEnd('.', ',', ';', ':', '!', '?', '"', '\'');
            if (candidate.Length < minLength)
            {
                continue;
            }

            // Skip ALL-CAPS shouting ("LOOK OUT") — not useful as entity names.
            if (candidate.Length > 1 && candidate.All(c => !char.IsLetter(c) || char.IsUpper(c))
                && candidate.Any(char.IsLetter)
                && !candidate.Contains(' '))
            {
                // Allow short acronyms like "NASA", "UN", "UK" (2–5 letters).
                if (candidate.Length is < 2 or > 5)
                {
                    continue;
                }
            }

            var tokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.All(StopWords.Contains))
            {
                continue;
            }

            // Drop leading stop words: "The Louvre" stays "Louvre" only if we want
            // single tokens — keep "New York" intact; strip only pure grammar words.
            while (tokens.Length > 1 && StopWords.Contains(tokens[0]))
            {
                tokens = tokens.Skip(1).ToArray();
            }

            if (tokens.Length == 1 && StopWords.Contains(tokens[0]))
            {
                continue;
            }

            candidate = string.Join(' ', tokens);
            if (candidate.Length < minLength)
            {
                continue;
            }

            // Sentence-start single words are often just normal words ("Then we left").
            // Keep them only when they are mid-sentence or multi-word phrases.
            // Example: "This is from France" → France is mid-sentence → keep.
            // Example: "France is beautiful" → France at start, single word → still keep
            // if not a stop word (countries often start sentences). Wikipedia filters false positives.
            if (tokens.Length == 1 && IsLikelySentenceStartNoise(normalized, match.Index, candidate))
            {
                continue;
            }

            if (!seen.Add(candidate))
            {
                continue;
            }

            results.Add(candidate);
        }

        return results;
    }

    /// <summary>
    /// Returns true when a single capitalized word at the start of a clause is
    /// probably grammar, not a named entity (e.g. "Then", already stopped, or
    /// generic openers). Real entities at sentence start still pass if not stop words;
    /// Wikipedia confirmation removes most remaining false positives.
    /// </summary>
    private static bool IsLikelySentenceStartNoise(string text, int matchIndex, string candidate)
    {
        if (StopWords.Contains(candidate))
        {
            return true;
        }

        // If preceded by a letter/digit, it's mid-sentence → good signal ("from France").
        for (var i = matchIndex - 1; i >= 0; i--)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c is '"' or '\'' or '«' or '»')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                return false;
            }

            // After . ? ! — sentence start. Allow through; Wikipedia is the gate.
            return false;
        }

        // Start of cue — allow through for places/names; stop words already filtered.
        return false;
    }

    // Capitalized sequences: "France", "New York", "United Nations"
    // Also short acronyms: "NASA", "FBI"
    [GeneratedRegex(@"\b(?:[A-Z]{2,5}|[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProperNounRegex();

    [GeneratedRegex(@"\.{2,}|…", RegexOptions.CultureInvariant)]
    private static partial Regex EllipsisRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
