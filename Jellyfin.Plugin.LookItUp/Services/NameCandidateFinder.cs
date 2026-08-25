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
    /// <param name="excludeNames">Cast/character names to exclude (case-insensitive).</param>
    /// <param name="minLength">Minimum term length.</param>
    /// <param name="maxCandidates">Max results to return.</param>
    IReadOnlyList<NameCandidate> Find(
        IReadOnlyList<SubtitleCue> cues,
        string? itemTitle,
        IReadOnlySet<string>? excludeNames,
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
        // Contractionsictions / dialogue false positives (I'm often looks title-case).
        "i'm", "im", "i've", "ive", "i'd", "i'll",
        "you're", "youre", "you've", "youve", "you'd", "youd", "you'll", "youll",
        "he's", "hes", "she's", "shes", "we're", "they're", "theyre",
        "it's", "that's", "thats", "what's", "whats", "who's", "whos", "there's", "theres",
        "here's", "heres", "let's", "lets", "don't", "dont", "can't", "cant", "won't", "wont",
        "all", "any", "some", "every", "each", "both", "few", "more", "most", "other", "such",
        "just", "also", "only", "even", "still", "already", "yet", "very", "too", "quite",
        "up", "down", "out", "off", "back", "away", "here", "now", "then", "once",
        "get", "got", "go", "goes", "went", "come", "came", "see", "saw", "look", "let", "make", "made",
        "know", "think", "want", "like", "take", "give", "tell", "say", "said", "ask"
    };

    /// <summary>
    /// Lowercase (or any-case) particles that appear inside real names: Vincent van Gogh, Ludwig van Beethoven.
    /// </summary>
    private static readonly HashSet<string> NameParticles = new(StringComparer.OrdinalIgnoreCase)
    {
        "van", "von", "de", "da", "dal", "del", "della", "delle", "dei", "degli", "di", "du",
        "la", "le", "el", "al", "bin", "ibn", "af", "av", "der", "den", "ten", "ter",
        "y", "e", "san", "santa", "saint", "st"
    };

    /// <summary>
    /// Subtitle noise and non-entity junk — not cultural-reference judgments (those go to AI).
    /// </summary>
    private static readonly HashSet<string> SubtitleNoiseTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        // Subtitle file credits / noise
        "opensubtitles", "opensubtitle", "subtitles", "subtitle", "subs", "synced", "sync",
        "english", "eng", "srt", "webrip", "web-dl", "bluray", "hdtv",
        // Sound / stage directions often capitalized in subs
        "thud", "bang", "boom", "beep", "ring", "knock", "sighs", "gasps", "laughs", "applause",
        // Filler caps / dialogue noise
        "ok", "okay", "oh", "uh", "um", "ah", "hmm", "huh", "wow", "whoa",
        "one", "two", "three", "first", "last", "next", "right", "left",
        "everything", "nothing", "something", "anything", "everyone", "someone",
        "through", "without", "feeling", "playing", "winning", "office", "screaming"
    };

    /// <inheritdoc />
    public IReadOnlyList<NameCandidate> Find(
        IReadOnlyList<SubtitleCue> cues,
        string? itemTitle,
        IReadOnlySet<string>? excludeNames,
        int minLength,
        int maxCandidates)
    {
        if (cues.Count == 0)
        {
            return [];
        }

        minLength = Math.Max(2, minLength);
        maxCandidates = Math.Max(1, maxCandidates);
        var exclude = excludeNames is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(excludeNames, StringComparer.OrdinalIgnoreCase);
        HarvestSpeakerLabels(exclude, cues, minLength);
        excludeNames = exclude;

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
                    if (token.EndsSentence || token.BreaksPhrase)
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

                // Skip ALL-CAPS speaker labels (ANNOUNCER, MAN, etc.).
                if (token.IsAllCapsWord)
                {
                    sentenceInitial = true; // next word starts a new utterance
                    continue;
                }

                if (token.IsTitleCaseWord)
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

        // Cap(+particle)+Cap phrases (Vincent van Gogh, Jon Voight), first occurrence.
        // Skip dialogue particles as phrase heads so "Uh… Vincent van Gogh" → Vincent van Gogh, not Uh….
        foreach (var group in occurrences.GroupBy(o => o.CueIndex))
        {
            AddCapPhrasesFromOccurrences(
                group.OrderBy(o => o.TokenIndex).ToList(),
                candidates,
                tokenStats,
                titleNorm,
                excludeNames,
                minLength);
        }

        // Subtitle wraps: "Vincent Van" / "Gogh." on consecutive cues.
        AddCrossCueParticlePhrases(
            cues,
            occurrences,
            candidates,
            tokenStats,
            titleNorm,
            excludeNames,
            minLength);

        // Single tokens with mid-sentence capitalization evidence.
        foreach (var occ in occurrences)
        {
            if (FunctionWords.Contains(occ.Surface) || IsExcludedName(occ.Surface, excludeNames))
            {
                continue;
            }

            if (!tokenStats.TryGetValue(occ.Surface, out var stats))
            {
                continue;
            }

            var mid = stats.MidSentenceCapCount;
            var strong = mid > 0;
            var repeatedCapOnly = mid == 0
                                  && stats.LowercaseCount == 0
                                  && stats.SentenceInitialCapCount + stats.MidSentenceCapCount >= 2;

            if (!strong && !repeatedCapOnly)
            {
                // Repeated at sentence starts (e.g. a character name always capitalized after a period).
                var repeatedInitial = stats.SentenceInitialCapCount >= 2
                                      && stats.MidSentenceCapCount == 0
                                      && stats.LowercaseCount == 0;
                if (!repeatedInitial)
                {
                    continue;
                }

                TryAddCandidate(
                    candidates,
                    occ.Surface,
                    occ,
                    tokenStats,
                    titleNorm,
                    excludeNames,
                    minLength,
                    scoreBonus: 8,
                    reason: "repeated-sentence-initial");
                continue;
            }

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
                excludeNames,
                minLength,
                scoreBonus: bonus,
                reason: reason);
        }

        // Unique by term (dictionary key); prefer earliest timestamp, highest score.
        return candidates.Values
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.StartMs)
            .ThenByDescending(c => c.Term.Count(ch => ch == ' '))
            .Where(c => !IsCoveredByLongerPhrase(c, candidates.Values))
            .GroupBy(c => c.Term, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(c => c.StartMs).First())
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.StartMs)
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
            for (var i = 0; i < parts.Length; i++)
            {
                if (!parts[i].Equals(candidate.Term, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Token after a possessive ("…'s LeBaron") is its own entity — keep it.
                if (i > 0 && IsPossessiveToken(parts[i - 1]))
                {
                    continue;
                }

                if (i > 0 && parts.Take(i).Any(IsPossessiveToken))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool IsPossessiveToken(string token)
    {
        return token.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
               || token.EndsWith("’s", StringComparison.OrdinalIgnoreCase)
               || token.EndsWith("'", StringComparison.Ordinal)
               || token.EndsWith("’", StringComparison.Ordinal);
    }

    private static void TryAddCandidate(
        Dictionary<string, NameCandidate> candidates,
        string term,
        Occurrence occ,
        Dictionary<string, TokenStats> tokenStats,
        string? titleNorm,
        IReadOnlySet<string> excludeNames,
        int minLength,
        int scoreBonus,
        string reason)
    {
        term = WhitespaceRegex().Replace(term.Trim(), " ");
        if (term.Length < minLength)
        {
            return;
        }

        // Strip leading dialogue Caps glued onto names ("Uh Vincent van Gogh").
        var split = term.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (split.Count > 0
               && FunctionWords.Contains(split[0])
               && !NameParticles.Contains(split[0]))
        {
            split.RemoveAt(0);
        }

        if (split.Count == 0)
        {
            return;
        }

        term = string.Join(' ', split);
        if (term.Length < minLength)
        {
            return;
        }

        // Never keep multi-clause dialogue fragments ("Wonderful, Hank …").
        if (term.Contains(',', StringComparison.Ordinal)
            || term.Contains(';', StringComparison.Ordinal)
            || term.Contains('?', StringComparison.Ordinal)
            || term.Contains('!', StringComparison.Ordinal))
        {
            return;
        }

        if (IsContractionOrDialogueParticle(term))
        {
            return;
        }

        if (IsSubtitleNoise(term))
        {
            return;
        }

        if (IsAllCapsTerm(term))
        {
            return;
        }

        if (IsFunctionOnlyPhrase(term))
        {
            return;
        }

        if (MatchesTitle(term, titleNorm) || IsExcludedName(term, excludeNames))
        {
            return;
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

            // Keep earliest appearance for the unique entry.
            if (occ.StartMs < existing.StartMs)
            {
                existing.StartMs = occ.StartMs;
                existing.EndMs = occ.EndMs;
                existing.CueText = occ.CueText;
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

    private static bool IsAllCapsTerm(string term)
    {
        var letters = term.Where(char.IsLetter).ToArray();
        return letters.Length >= 2 && letters.All(char.IsUpper);
    }

    private static bool IsExcludedName(string term, IReadOnlySet<string> excludeNames)
    {
        if (excludeNames.Count == 0)
        {
            return false;
        }

        foreach (var candidate in ExpandExcludeForms(term))
        {
            if (excludeNames.Contains(candidate))
            {
                return true;
            }

            var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                if (excludeNames.Contains(parts[0]))
                {
                    return true;
                }
            }
            else if (parts.All(excludeNames.Contains))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Yields the term plus possessive-stripped forms ("D'Angelo's" → "D'Angelo").
    /// </summary>
    private static IEnumerable<string> ExpandExcludeForms(string term)
    {
        var t = term.Trim();
        if (t.Length == 0)
        {
            yield break;
        }

        yield return t;

        // Possessive: D'Angelo's / D’Angelo’s / D'Angelos (subtitle OCR)
        var stripped = StripTrailingPossessive(t);
        if (!string.Equals(stripped, t, StringComparison.OrdinalIgnoreCase)
            && stripped.Length > 0)
        {
            yield return stripped;
        }
    }

    private static string StripTrailingPossessive(string term)
    {
        if (term.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
            || term.EndsWith("’s", StringComparison.OrdinalIgnoreCase)
            || term.EndsWith("‘s", StringComparison.OrdinalIgnoreCase))
        {
            return term[..^2].TrimEnd();
        }

        if (term.EndsWith('\'') || term.EndsWith('’') || term.EndsWith('‘'))
        {
            return term[..^1].TrimEnd();
        }

        return term;
    }

    private static void AddCapPhrasesFromOccurrences(
        IReadOnlyList<Occurrence> list,
        Dictionary<string, NameCandidate> candidates,
        Dictionary<string, TokenStats> tokenStats,
        string? titleNorm,
        IReadOnlySet<string> excludeNames,
        int minLength)
    {
        var i = 0;
        while (i < list.Count)
        {
            // "Uh / And / But … Name" — dialogue Caps are not name heads.
            if (FunctionWords.Contains(list[i].Surface))
            {
                i++;
                continue;
            }

            var start = list[i];
            var parts = new List<string> { start.Surface };
            var j = i + 1;
            while (j < list.Count
                   && TryExtendNamePhrase(list[j - 1], list[j], out var middleParticles))
            {
                if (middleParticles is { Count: > 0 })
                {
                    parts.AddRange(middleParticles);
                }

                parts.Add(list[j].Surface);
                j++;
            }

            if (parts.Count >= 2)
            {
                TryAddCandidate(
                    candidates,
                    string.Join(' ', parts),
                    start,
                    tokenStats,
                    titleNorm,
                    excludeNames,
                    minLength,
                    scoreBonus: 40,
                    reason: "cap-phrase");

                // "Jon Voight's LeBaron" → also keep "LeBaron" (brand/object after 's).
                for (var p = 0; p < parts.Count - 1; p++)
                {
                    if (!IsPossessiveToken(parts[p]))
                    {
                        continue;
                    }

                    var tailParts = parts.Skip(p + 1).Where(t => !NameParticles.Contains(t)).ToList();
                    if (tailParts.Count == 0)
                    {
                        continue;
                    }

                    var tailOcc = list[Math.Min(i + p + 1, list.Count - 1)];
                    TryAddCandidate(
                        candidates,
                        string.Join(' ', tailParts),
                        tailOcc,
                        tokenStats,
                        titleNorm,
                        excludeNames,
                        minLength,
                        scoreBonus: 35,
                        reason: "possessive-tail");
                }
            }

            i = Math.Max(i + 1, j);
        }
    }

    /// <summary>
    /// Joins Cap(+particle) at the end of one cue with Cap(s) at the start of the next
    /// when a wrap splits names like "Vincent Van" / "Gogh.".
    /// </summary>
    private static void AddCrossCueParticlePhrases(
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlyList<Occurrence> occurrences,
        Dictionary<string, NameCandidate> candidates,
        Dictionary<string, TokenStats> tokenStats,
        string? titleNorm,
        IReadOnlySet<string> excludeNames,
        int minLength)
    {
        const long maxGapMs = 2500;
        var byCue = occurrences
            .GroupBy(o => o.CueIndex)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.TokenIndex).ToList());

        for (var cueIndex = 0; cueIndex < cues.Count - 1; cueIndex++)
        {
            if (!byCue.TryGetValue(cueIndex, out var leftCaps) || leftCaps.Count == 0)
            {
                continue;
            }

            if (!byCue.TryGetValue(cueIndex + 1, out var rightCaps) || rightCaps.Count == 0)
            {
                continue;
            }

            if (cues[cueIndex + 1].StartMs - cues[cueIndex].EndMs > maxGapMs)
            {
                continue;
            }

            var leftTokens = leftCaps[0].Tokens;
            var rightTokens = rightCaps[0].Tokens;
            if (leftTokens is null || rightTokens is null)
            {
                continue;
            }

            var lastLeft = leftCaps[^1];
            if (FunctionWords.Contains(lastLeft.Surface) && !NameParticles.Contains(lastLeft.Surface))
            {
                continue;
            }

            // No sentence end after the last Cap on the left cue.
            var leftEnded = false;
            for (var t = lastLeft.TokenIndex + 1; t < leftTokens.Count; t++)
            {
                if (leftTokens[t].EndsSentence)
                {
                    leftEnded = true;
                    break;
                }
            }

            if (leftEnded)
            {
                continue;
            }

            // Left must end on a name particle (TitleCase "Van" or lowercase "van" after Cap).
            var trailingParticles = new List<string>();
            if (NameParticles.Contains(lastLeft.Surface))
            {
                // particle already included as Cap occurrence
            }
            else
            {
                for (var t = lastLeft.TokenIndex + 1; t < leftTokens.Count; t++)
                {
                    var tok = leftTokens[t];
                    if (tok.IsPunctuationOnly)
                    {
                        if (tok.EndsSentence || tok.BreaksPhrase)
                        {
                            trailingParticles.Clear();
                            break;
                        }

                        continue;
                    }

                    if (NameParticles.Contains(tok.Surface))
                    {
                        trailingParticles.Add(tok.Surface);
                        continue;
                    }

                    trailingParticles.Clear();
                    break;
                }

                if (trailingParticles.Count == 0)
                {
                    continue;
                }
            }

            // Collect contiguous Cap(+particle) run ending at lastLeft, skipping function heads.
            var leftRun = new List<Occurrence>();
            for (var i = leftCaps.Count - 1; i >= 0; i--)
            {
                var occ = leftCaps[i];
                if (leftRun.Count == 0)
                {
                    leftRun.Add(occ);
                    continue;
                }

                if (!TryExtendNamePhrase(occ, leftRun[0], out _))
                {
                    break;
                }

                leftRun.Insert(0, occ);
            }

            while (leftRun.Count > 0 && FunctionWords.Contains(leftRun[0].Surface)
                                     && !NameParticles.Contains(leftRun[0].Surface))
            {
                leftRun.RemoveAt(0);
            }

            if (leftRun.Count == 0)
            {
                continue;
            }

            var parts = new List<string>();
            for (var i = 0; i < leftRun.Count; i++)
            {
                if (i > 0)
                {
                    if (!TryExtendNamePhrase(leftRun[i - 1], leftRun[i], out var mid))
                    {
                        parts.Clear();
                        break;
                    }

                    if (mid is { Count: > 0 })
                    {
                        parts.AddRange(mid);
                    }
                }

                parts.Add(leftRun[i].Surface);
            }

            if (parts.Count == 0)
            {
                continue;
            }

            parts.AddRange(trailingParticles);

            var rightStart = rightCaps[0];
            if (FunctionWords.Contains(rightStart.Surface))
            {
                continue;
            }

            // Next cue should start with Cap (optionally after punctuation only).
            for (var t = 0; t < rightStart.TokenIndex; t++)
            {
                var tok = rightTokens[t];
                if (tok.IsPunctuationOnly)
                {
                    continue;
                }

                // Content before first Cap → not a wrap continuation.
                goto SkipCue;
            }

            parts.Add(rightStart.Surface);
            for (var rj = 1; rj < rightCaps.Count; rj++)
            {
                if (!TryExtendNamePhrase(rightCaps[rj - 1], rightCaps[rj], out var midRight))
                {
                    break;
                }

                if (midRight is { Count: > 0 })
                {
                    parts.AddRange(midRight);
                }

                parts.Add(rightCaps[rj].Surface);
            }

            if (parts.Count >= 2)
            {
                TryAddCandidate(
                    candidates,
                    string.Join(' ', parts),
                    leftRun[0],
                    tokenStats,
                    titleNorm,
                    excludeNames,
                    minLength,
                    scoreBonus: 45,
                    reason: "cap-phrase-wrap");
            }

            SkipCue:
            ;
        }
    }

    private static bool TryExtendNamePhrase(
        Occurrence left,
        Occurrence right,
        out List<string>? middleParticles)
    {
        middleParticles = null;

        // Tokens list is shared per cue; allow only punctuation and name particles between Caps.
        if (left.Tokens is null || !ReferenceEquals(left.Tokens, right.Tokens))
        {
            return right.TokenIndex == left.TokenIndex + 1;
        }

        if (right.TokenIndex <= left.TokenIndex)
        {
            return false;
        }

        for (var i = left.TokenIndex + 1; i < right.TokenIndex; i++)
        {
            var tok = left.Tokens[i];
            if (tok.EndsSentence || tok.BreaksPhrase)
            {
                return false;
            }

            if (tok.IsPunctuationOnly)
            {
                continue;
            }

            if (NameParticles.Contains(tok.Surface))
            {
                middleParticles ??= [];
                middleParticles.Add(tok.Surface);
                continue;
            }

            // Any other lowercase/content word breaks the name phrase.
            return false;
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
        // Strip SDH speaker labels so "[Brewmaster Yun] Her knife…" does not become "Brewmaster Yun Her".
        t = SpeakerBracketRegex().Replace(t, " ");
        t = WhitespaceRegex().Replace(t, " ").Trim();
        return t;
    }

    /// <summary>
    /// Collects <c>[Speaker Name]</c> labels from cues into the exclude set.
    /// </summary>
    private static void HarvestSpeakerLabels(
        HashSet<string> exclude,
        IReadOnlyList<SubtitleCue> cues,
        int minLength)
    {
        foreach (var cue in cues)
        {
            if (string.IsNullOrWhiteSpace(cue.Text))
            {
                continue;
            }

            foreach (Match m in SpeakerBracketRegex().Matches(cue.Text))
            {
                var speaker = m.Groups[1].Value.Trim();
                if (speaker.Length < minLength || !speaker.Any(char.IsUpper))
                {
                    continue;
                }

                // Skip pure stage directions: [sighs], [applause playing]
                var letterCount = speaker.Count(char.IsLetter);
                if (letterCount < minLength)
                {
                    continue;
                }

                exclude.Add(speaker);
                exclude.Add(speaker + "'s");
                exclude.Add(speaker + "’s");
                foreach (var part in speaker.Split(
                             [' ', '/', ',', '-', '—', '–'],
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (part.Length >= minLength && part.Any(char.IsUpper))
                    {
                        exclude.Add(part);
                        exclude.Add(part + "'s");
                        exclude.Add(part + "’s");
                    }
                }
            }
        }
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
            // Commas / semicolons / closing brackets break Cap+Cap phrases
            // ("Wonderful, Hank" / end of "[Brewmaster Yun]" must not glue to the next word).
            var breaksPhrase = surface is ":" or "-" or "—" or "–" or "," or ";" or "]" or ")"
                               || surface.EndsWith(':')
                               || surface.EndsWith(',')
                               || surface.EndsWith(';')
                               || surface.EndsWith(']')
                               || surface.EndsWith(')');

            // Strip trailing sentence punctuation from word surface for matching.
            var word = surface.TrimEnd('.', ',', ';', ':', '!', '?', '"', '\'', ')', ']');
            word = word.TrimStart('"', '\'', '(', '[');

            if (word.Length == 0
                || surface is "," or ";" or ":" or "-" or "—" or "–" or "." or "!" or "?" or "…")
            {
                tokens.Add(new Token
                {
                    Surface = surface,
                    IsPunctuationOnly = true,
                    EndsSentence = endsSentence,
                    BreaksPhrase = breaksPhrase || endsSentence || surface is "," or ";"
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
                EndsSentence = endsSentence,
                BreaksPhrase = breaksPhrase
            });
        }

        return tokens;
    }

    [GeneratedRegex(@"\[([^\]]{2,60})\]", RegexOptions.CultureInvariant)]
    private static partial Regex SpeakerBracketRegex();

    [GeneratedRegex(@"\b[\p{L}][\p{L}\p{Mn}']*\b|[,;:!?.…\-—–\[\]\(\)]", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\.{2,}|…", RegexOptions.CultureInvariant)]
    private static partial Regex EllipsisRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private static bool IsSubtitleNoise(string term)
    {
        if (SubtitleNoiseTerms.Contains(term))
        {
            return true;
        }

        // "OpenSubtitles.com" / "www.OpenSubtitles.org"
        var compact = term.Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Contains("opensubtitle", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("subtitles", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContractionOrDialogueParticle(string term)
    {
        var t = term.Trim().TrimEnd('.', ',', '!', '?', ';', ':');
        return FunctionWords.Contains(t);
    }

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

        public bool BreaksPhrase { get; init; }
    }
}
