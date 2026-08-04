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
        "Today", "Tomorrow", "Yesterday", "Tonight", "Morning", "Night", "God", "Hell", "Damn",
        // Dialogue / subtitle shouting noise that Wikipedia often still resolves.
        "All", "Yeah", "Yah", "Yep", "Nah", "Huh", "Heh", "Hah", "Ha", "Whoa", "Wow",
        "Done", "Away", "Seem", "Let", "Now", "Take", "Lie", "Thud", "Come", "Go", "Get",
        "Got", "See", "Saw", "Look", "Listen", "Wait", "Stop", "Start", "End", "Back",
        "Out", "Off", "Up", "Down", "Man", "Boy", "Girl", "Guy", "Kid", "Dude",
        "Mom", "Dad", "Pop", "Buddy", "Pal", "Honey", "Baby", "Dear",
        "New", "Old", "Big", "Little", "Good", "Bad", "Fine", "Nice", "Great",
        "Limited", "Consumer", "Street"
    };

    /// <summary>
    /// Short ALL-CAPS tokens allowed through (real acronyms). Everything else
    /// matching the 2–5 letter caps pattern is treated as shouting ("ALL", "YES").
    /// </summary>
    private static readonly HashSet<string> AcronymAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "NASA", "FBI", "CIA", "NSA", "BBC", "CNN", "NBC", "ABC", "CBS", "HBO",
        "USA", "UK", "UN", "EU", "USSR", "NYC", "LA", "SF", "DC",
        "IBM", "BMW", "VW", "GM", "GE", "ATF", "DEA", "IRS", "DMV",
        "NFL", "NBA", "MLB", "NHL", "UFC", "WWE", "MIT", "UCLA", "NYU"
    };

    /// <inheritdoc />
    public IReadOnlyList<string> Extract(string text, int minLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

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

            // ALL-CAPS single token: only keep known acronyms (not "ALL", "YES", "NOW").
            if (!candidate.Contains(' ')
                && candidate.All(c => !char.IsLetter(c) || char.IsUpper(c))
                && candidate.Any(char.IsLetter))
            {
                if (!AcronymAllowlist.Contains(candidate))
                {
                    continue;
                }
            }

            var tokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.All(StopWords.Contains))
            {
                continue;
            }

            while (tokens.Length > 1 && StopWords.Contains(tokens[0]))
            {
                tokens = tokens.Skip(1).ToArray();
            }

            while (tokens.Length > 1 && StopWords.Contains(tokens[^1]))
            {
                tokens = tokens.Take(tokens.Length - 1).ToArray();
            }

            if (tokens.Length == 0 || (tokens.Length == 1 && StopWords.Contains(tokens[0])))
            {
                continue;
            }

            candidate = string.Join(' ', tokens);
            if (candidate.Length < minLength || !seen.Add(candidate))
            {
                continue;
            }

            results.Add(candidate);
        }

        // Prefer multi-word names first within the cue (Jon Voight before Car).
        return results
            .OrderByDescending(r => r.Count(c => c == ' '))
            .ThenByDescending(r => r.Length)
            .ToList();
    }

    // Title Case phrases + allowlisted-style acronyms (filtered in code).
    [GeneratedRegex(@"\b(?:[A-Z]{2,6}|[A-Z][a-z]+(?:\s+[A-Z][a-z]+){0,4})\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProperNounRegex();

    [GeneratedRegex(@"\.{2,}|…", RegexOptions.CultureInvariant)]
    private static partial Regex EllipsisRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
