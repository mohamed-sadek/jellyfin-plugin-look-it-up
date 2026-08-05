using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Finds name-like candidates from a full subtitle document without AI.
/// </summary>
public interface INameCandidateFinder
{
    /// <summary>
    /// Scans all cues using document-level capitalization evidence.
    /// </summary>
    /// <param name="cues">Timed subtitle cues.</param>
    /// <param name="itemTitle">Media title to exclude (episode/movie name).</param>
    /// <param name="minLength">Minimum term length.</param>
    /// <param name="maxCandidates">Max results to return.</param>
    IReadOnlyList<NameCandidate> Find(
        IReadOnlyList<SubtitleCue> cues,
        string? itemTitle,
        int minLength,
        int maxCandidates);
}

/// <summary>
/// Document-level capitalization name finder for English subtitles.
/// Prefers mid-sentence capitals and Cap+Cap phrases; allows single names.
/// Uses only a small closed-class function-word filter (not an entity allowlist).
/// </summary>
public partial class NameCandidateFinder : INameCandidateFinder
{
    /// <summary>
    /// English function / closed-class words — not entity knowledge.
    /// </summary>
    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "this", "that", "these", "those", "there", "here",
        "i", "you", "he", "she", "we", "they", "it", "me", "him", "her", "us", "them",
        "my", "your", "his", "our", "their", "mine", "yours", "hers", "ours", "theirs",
        "and", "or", "but", "if", "then", "so", "because", "as", "of", "in", "on", "at", "to", "for",
        "from", "with", "about", "into", "over", "after", "before", "between", "under", "again",
        "yes", "no", "ok", "okay", "hey", "hi", "hello", "please", "sorry", "well", "oh", "ah", "uh", "um",
        "what", "when", "where", "who", "why", "how", "which", "whom", "whose",
        "is", "are", "was", "were", "be", "been", "being", "am",
        "have", "has", "had", "do", "does", "did", "done",
        "will", "would", "could", "should", "may", "might", "must", "can", "shall",
        "not", "don", "didn", "isn", "aren", "wasn", "weren", "hasn", "haven", "hadn", "won", "wouldn",
        "mr", "mrs", "ms", "dr",
        "all", "any", "some", "every", "each", "both", "few", "more", "most", "other", "such",
        "just", "also", "only", "even", "still", "already", "yet", "very", "too", "quite",
        "up", "down", "out", "off", "back", "away", "here", "now", "then", "once",
        "get", "got", "go", "goes", "went", "come", "came", "see", "saw", "look", "let", "make", "made",
        "know", "think", "want", "like", "take", "give", "tell", "say", "said", "ask"
    };

    /// <inheritdoc />
    public IReadOnlyList<NameCandidate> Find(
        IReadOnlyList<SubtitleCue> cues,
        string? itemTitle,
        int minLength,
        int maxCandidates)
    {
        if (cues.Count == 0)
        {
            return [];
        }

        minLength = Math.Max(2, minLength);
        maxCandidates = Math.Max(1, maxCandidates);

        var titleNorm = NormalizeTitle(itemTitle);
        var tokenStats = new Dictionary<string, TokenStats>(StringComparer.OrdinalIgnoreCase);
        var occurrences = new List<Occurrence>();

        for (var cueIndex = 0; cueIndex < cues.Count; cueIndex++)
        {
            var cue = cues[cueIndex];
            var line = NormalizeCueText(cue.Text);
            if (line.Length == 0)
            {
                continue;
            }

            // Whole-cue shouting — skip.
            if (IsMostlyAllCaps(line))
            {
                continue;
            }

            var tokens = Tokenize(line);
            var sentenceInitial = true;

            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.IsPunctuationOnly)
                {
                    if (token.EndsSentence)
                    {
                        sentenceInitial = true;
                    }

                    continue;
                }

                var key = token.Surface;
                if (!tokenStats.TryGetValue(key, out var stats))
                {
                    stats = new TokenStats();
                    tokenStats[key] = stats;
                }

                if (token.HasLetter)
                {
                    if (token.IsLower)
                    {
                        stats.LowercaseCount++;
                    }
                    else if (token.IsTitleOrAllCaps)
                    {
                        if (sentenceInitial)
                        {
                            stats.SentenceInitialCapCount++;
                        }
                        else
                        {
                            stats.MidSentenceCapCount++;
                        }
                    }
                }

                if (token.IsTitleCaseWord || token.IsAllCapsWord)
                {
                    occurrences.Add(new Occurrence
                    {
                        CueIndex = cueIndex,
                        TokenIndex = i,
                        Surface = token.Surface,
                        StartMs = cue.StartMs,
                        EndMs = cue.EndMs,
                        CueText = line,
                        SentenceInitial = sentenceInitial,
                        Tokens = tokens
                    });
                }

                sentenceInitial = token.EndsSentence;
            }
        }

        var candidates = new Dictionary<string, NameCandidate>(StringComparer.OrdinalIgnoreCase);

        // Cap+Cap phrases (and longer Cap runs), first occurrence.
        foreach (var group in occurrences.GroupBy(o => o.CueIndex))
        {
            var list = group.OrderBy(o => o.TokenIndex).ToList();
            var i = 0;
            while (i < list.Count)
            {
                var start = list[i];
                var parts = new List<string> { start.Surface };
                var j = i + 1;
                while (j < list.Count
                       && list[j].TokenIndex == list[j - 1].TokenIndex + 1
                       && AreAdjacentContentTokens(list[j - 1], list[j]))
                {
                    parts.Add(list[j].Surface);
                    j++;
                }

                if (parts.Count >= 2)
                {
                    var term = string.Join(' ', parts);
                    TryAddCandidate(
                        candidates,
                        term,
                        start,
                        tokenStats,
                        titleNorm,
                        minLength,
                        scoreBonus: 40,
                        reason: "cap-phrase");
                }

                i = Math.Max(i + 1, j);
            }
        }

        // Single tokens with mid-sentence capitalization evidence.
        foreach (var occ in occurrences)
        {
            if (FunctionWords.Contains(occ.Surface))
            {
                continue;
            }

            if (!tokenStats.TryGetValue(occ.Surface, out var stats))
            {
                continue;
            }

            // Strong: seen capitalized mid-sentence at least once.
            // Weaker: never lowercase and appears capitalized more than once (often names in dialogue).
            var mid = stats.MidSentenceCapCount;
            var strong = mid > 0;
            var repeatedCapOnly = mid == 0
                                  && stats.LowercaseCount == 0
                                  && stats.SentenceInitialCapCount + stats.MidSentenceCapCount >= 2;

            if (!strong && !repeatedCapOnly)
            {
                continue;
            }

            // Prefer the mid-sentence occurrence as the timestamp when available.
            if (occ.SentenceInitial && mid > 0)
            {
                continue;
            }

            var reason = strong ? "mid-sentence-cap" : "repeated-cap-no-lower";
            var bonus = strong ? 25 : 10;
            TryAddCandidate(
                candidates,
                occ.Surface,
                occ,
                tokenStats,
                titleNorm,
                minLength,
                scoreBonus: bonus,
                reason: reason);
        }

        return candidates.Values
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.StartMs)
            .ThenByDescending(c => c.Term.Count(ch => ch == ' '))
            .Where(c => !IsCoveredByLongerPhrase(c, candidates.Values))
            .Take(maxCandidates)
            .ToList();
    }

    private static bool IsCoveredByLongerPhrase(NameCandidate candidate, IEnumerable<NameCandidate> all)
    {
        if (candidate.Term.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var other in all)
        {
            if (ReferenceEquals(other, candidate) || other.Score < candidate.Score - 5)
            {
                continue;
            }

            if (!other.Term.Contains(' ', StringComparison.Ordinal))
            {
                continue;
            }

            var parts = other.Term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => p.Equals(candidate.Term, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryAddCandidate(
        Dictionary<string, NameCandidate> candidates,
        string term,
        Occurrence occ,
        Dictionary<string, TokenStats> tokenStats,
        string? titleNorm,
        int minLength,
        int scoreBonus,
        string reason)
    {
        term = term.Trim();
        if (term.Length < minLength)
        {
            return;
        }

        if (IsFunctionOnlyPhrase(term))
        {
            return;
        }

        if (MatchesTitle(term, titleNorm))
        {
            return;
        }

        // Skip pure ALL-CAPS shouting tokens (keep Title Case / mixed).
        if (!term.Contains(' ', StringComparison.Ordinal)
            && term.Length <= 6
            && term.All(c => !char.IsLetter(c) || char.IsUpper(c))
            && term.Any(char.IsLetter)
            && term.Any(char.IsLower) == false
            && term.Length >= 2)
        {
            // Short ALL-CAPS mid-sentence can still be acronyms; keep if mid-sentence evidence exists.
            var head = term.Split(' ')[0];
            if (!tokenStats.TryGetValue(head, out var st) || st.MidSentenceCapCount == 0)
            {
                return;
            }
        }

        var headToken = term.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        tokenStats.TryGetValue(headToken, out var headStats);
        var midHits = headStats?.MidSentenceCapCount ?? 0;
        var score = scoreBonus
                    + (midHits * 5)
                    + (term.Count(c => c == ' ') * 8)
                    + Math.Min(term.Length, 24);

        if (candidates.TryGetValue(term, out var existing))
        {
            if (score > existing.Score)
            {
                existing.Score = score;
                existing.Reason = reason;
            }

            return;
        }

        candidates[term] = new NameCandidate
        {
            Term = term,
            StartMs = occ.StartMs,
            EndMs = occ.EndMs,
            CueText = occ.CueText,
            Score = score,
            Reason = reason,
            MidSentenceHits = midHits
        };
    }

    private static bool AreAdjacentContentTokens(Occurrence left, Occurrence right)
    {
        // Tokens list is shared per cue; ensure no non-space content between indexes.
        if (left.Tokens is null || !ReferenceEquals(left.Tokens, right.Tokens))
        {
            return right.TokenIndex == left.TokenIndex + 1;
        }

        for (var i = left.TokenIndex + 1; i < right.TokenIndex; i++)
        {
            if (!left.Tokens[i].IsPunctuationOnly)
            {
                return false;
            }

            // Hyphenated names ok; sentence break is not a phrase.
            if (left.Tokens[i].EndsSentence)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFunctionOnlyPhrase(string term)
    {
        var parts = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(FunctionWords.Contains);
    }

    private static bool MatchesTitle(string term, string? titleNorm)
    {
        if (string.IsNullOrEmpty(titleNorm))
        {
            return false;
        }

        var termNorm = NormalizeTitle(term);
        // Only drop the full title (or near-exact), not substrings like "Mom" / "Store".
        return !string.IsNullOrEmpty(termNorm) && termNorm == titleNorm;
    }

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string NormalizeCueText(string text)
    {
        var t = EllipsisRegex().Replace(text ?? string.Empty, " ");
        t = HtmlTagRegex().Replace(t, " ");
        t = WhitespaceRegex().Replace(t, " ").Trim();
        return t;
    }

    private static bool IsMostlyAllCaps(string line)
    {
        var letters = line.Where(char.IsLetter).ToList();
        if (letters.Count < 6)
        {
            return false;
        }

        var upper = letters.Count(char.IsUpper);
        return upper >= letters.Count * 0.85;
    }

    private static List<Token> Tokenize(string line)
    {
        var tokens = new List<Token>();
        foreach (Match m in TokenRegex().Matches(line))
        {
            var surface = m.Value;
            var letters = surface.Where(char.IsLetter).ToArray();
            var hasLetter = letters.Length > 0;
            var isAllCaps = hasLetter && letters.All(char.IsUpper);
            var isTitle = hasLetter
                          && char.IsUpper(letters[0])
                          && letters.Skip(1).Any(char.IsLower);
            var isLower = hasLetter && letters.All(char.IsLower);
            var endsSentence = surface is "." or "!" or "?" or "…"
                               || surface.EndsWith('.')
                               || surface.EndsWith('!')
                               || surface.EndsWith('?');

            // Strip trailing sentence punctuation from word surface for matching.
            var word = surface.TrimEnd('.', ',', ';', ':', '!', '?', '"', '\'', ')', ']');
            word = word.TrimStart('"', '\'', '(', '[');

            if (word.Length == 0)
            {
                tokens.Add(new Token
                {
                    Surface = surface,
                    IsPunctuationOnly = true,
                    EndsSentence = endsSentence
                });
                continue;
            }

            letters = word.Where(char.IsLetter).ToArray();
            hasLetter = letters.Length > 0;
            isAllCaps = hasLetter && letters.All(char.IsUpper);
            isTitle = hasLetter && char.IsUpper(letters[0]) && letters.Skip(1).Any(char.IsLower);
            isLower = hasLetter && letters.All(char.IsLower);

            tokens.Add(new Token
            {
                Surface = word,
                HasLetter = hasLetter,
                IsAllCapsWord = isAllCaps && word.Length >= 2,
                IsTitleCaseWord = isTitle,
                IsTitleOrAllCaps = isTitle || (isAllCaps && word.Length >= 2),
                IsLower = isLower,
                IsPunctuationOnly = !hasLetter,
                EndsSentence = endsSentence
            });
        }

        return tokens;
    }

    [GeneratedRegex(@"\b[\p{L}][\p{L}\p{Mn}']*\b|[.!?…]", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\.{2,}|…", RegexOptions.CultureInvariant)]
    private static partial Regex EllipsisRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed class TokenStats
    {
        public int MidSentenceCapCount { get; set; }

        public int SentenceInitialCapCount { get; set; }

        public int LowercaseCount { get; set; }
    }

    private sealed class Occurrence
    {
        public int CueIndex { get; init; }

        public int TokenIndex { get; init; }

        public string Surface { get; init; } = string.Empty;

        public long StartMs { get; init; }

        public long EndMs { get; init; }

        public string CueText { get; init; } = string.Empty;

        public bool SentenceInitial { get; init; }

        public List<Token>? Tokens { get; init; }
    }

    private sealed class Token
    {
        public string Surface { get; init; } = string.Empty;

        public bool HasLetter { get; init; }

        public bool IsAllCapsWord { get; init; }

        public bool IsTitleCaseWord { get; init; }

        public bool IsTitleOrAllCaps { get; init; }

        public bool IsLower { get; init; }

        public bool IsPunctuationOnly { get; init; }

        public bool EndsSentence { get; init; }
    }
}
