using System.Text.RegularExpressions;
using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Local keep/drop rules for batched name popups: vacuous summaries, fragment titles,
/// sequel markers, and cue words that the Wikipedia article never mentions.
/// </summary>
public static partial class BatchKeepPolicy
{
    private static readonly HashSet<string> DialogueWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "along", "already", "always", "another", "around", "because",
        "before", "being", "check", "could", "drive", "every", "first", "going", "gonna", "great",
        "guess", "kidding", "know", "maybe", "might", "minute", "never", "other", "please", "really",
        "right", "shadow", "should", "still", "style", "thank", "there", "these", "thing", "things",
        "think", "those", "under", "until", "where", "which", "while", "would", "yeah", "movie",
        "girls", "woman", "people"
    };

    /// <summary>
    /// True when the summary restates the cue and names no external fact.
    /// </summary>
    public static bool IsVacuousSummary(string term, string summary)
    {
        var body = StripTermPrefix(term, summary).Trim().TrimEnd('.', ' ').ToLowerInvariant();
        if (body.Length == 0)
        {
            return true;
        }

        if (body.Contains("mentioned in the dialogue", StringComparison.Ordinal)
            || body.Contains("person mentioned", StringComparison.Ordinal)
            || body.Contains("mentioned in dialogue", StringComparison.Ordinal)
            || body.Contains("likely a family", StringComparison.Ordinal)
            || body.Contains("is a relative", StringComparison.Ordinal)
            || body.Contains("family member", StringComparison.Ordinal))
        {
            return true;
        }

        return body is "is a painter" or "a painter" or "is a person" or "a person";
    }

    /// <summary>
    /// Drops a kept mention when a later decision in the same batch rejected that term.
    /// </summary>
    public static List<AiEntityMention> DropContradictedKeeps(
        IReadOnlyList<AiEntityMention> mentions,
        IReadOnlyList<AiVerifyDecision> decisions)
    {
        var finalKeep = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in decisions)
        {
            if (string.IsNullOrWhiteSpace(decision.Term)
                || string.Equals(decision.Category, "error", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            finalKeep[decision.Term.Trim()] = decision.Kept;
        }

        return mentions.Where(m => !finalKeep.TryGetValue(m.Term.Trim(), out var keep) || keep).ToList();
    }

    /// <summary>
    /// Keeps the longer title when two popups are slices of the same cue phrase.
    /// </summary>
    public static List<AiEntityMention> Collapse(
        IReadOnlyList<AiEntityMention> mentions,
        IReadOnlyList<AiVerifyDecision> decisions)
    {
        var cues = new Dictionary<long, string>();
        foreach (var decision in decisions)
        {
            if (!string.IsNullOrWhiteSpace(decision.CueText))
            {
                cues.TryAdd(decision.StartMs, decision.CueText);
            }
        }

        var drop = new HashSet<int>();
        for (var i = 0; i < mentions.Count; i++)
        {
            for (var j = 0; j < mentions.Count; j++)
            {
                if (i == j || drop.Contains(i))
                {
                    continue;
                }

                if (Math.Abs(mentions[i].StartMs - mentions[j].StartMs) > 8_000)
                {
                    continue;
                }

                var shorter = mentions[i].Term.Trim();
                var longer = mentions[j].Term.Trim();
                if (shorter.Length == 0 || longer.Length <= shorter.Length)
                {
                    continue;
                }

                if (TermContains(longer, shorter) || SharesTitleTail(shorter, longer))
                {
                    drop.Add(i);
                    continue;
                }

                var cue = cues.TryGetValue(mentions[i].StartMs, out var same)
                    ? same
                    : cues.TryGetValue(mentions[j].StartMs, out var other) ? other : null;
                if (cue is not null && IsLeadingFragment(shorter, longer, cue))
                {
                    drop.Add(i);
                }
            }
        }

        var kept = new List<AiEntityMention>(mentions.Count);
        for (var i = 0; i < mentions.Count; i++)
        {
            if (!drop.Contains(i))
            {
                kept.Add(mentions[i]);
            }
        }

        return kept;
    }

    /// <summary>
    /// Uses the cue's "Trader Joe's" when the model dropped the possessive.
    /// </summary>
    public static string RestorePossessive(string term, string? cue)
    {
        var trimmed = term.Trim();
        if (string.IsNullOrWhiteSpace(cue) || trimmed.Length == 0)
        {
            return trimmed;
        }

        if (trimmed.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (Regex.IsMatch(
                cue,
                Regex.Escape(trimmed) + "['’]s",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return trimmed + "'s";
        }

        return trimmed;
    }

    /// <summary>
    /// Wikipedia titles to try, sequel form first when the cue says "Blade 2" / "Blade II".
    /// </summary>
    public static IReadOnlyList<string> LookupTitles(string term, string? cue)
    {
        var titles = new List<string>();
        var marker = SequelMarker(term, cue);
        if (marker is not null)
        {
            var roman = ToRoman(marker);
            if (!string.IsNullOrEmpty(roman))
            {
                titles.Add(term + " " + roman);
            }

            if (!marker.Equals(roman, StringComparison.OrdinalIgnoreCase))
            {
                titles.Add(term + " " + marker);
            }
        }

        if (!titles.Contains(term, StringComparer.OrdinalIgnoreCase))
        {
            titles.Add(term);
        }

        return titles;
    }

    /// <summary>
    /// Why this article does not match the cue, or null when it should become the popup.
    /// </summary>
    public static string? ArticleMismatch(string term, string? cue, string title, string extract)
    {
        var marker = SequelMarker(term, cue);
        if (marker is not null && !TitleHasSequelMarker(title, marker))
        {
            return "Cue names a sequel this article does not.";
        }

        var domain = CueDomainTokens(term, cue);
        if (domain.Count > 0)
        {
            var blob = title + "\n" + extract;
            if (domain.All(token => blob.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0))
            {
                return "Wikipedia article does not mention the cue's other subject (" + domain[0] + ").";
            }
        }

        return null;
    }

    /// <summary>
    /// Adjusts kind when the article is a book series or a film the model called a person.
    /// </summary>
    public static string KindFromArticle(string kind, string title, string extract)
    {
        var blob = title + " " + extract;
        if (blob.Contains("book series", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("children's book", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("novel series", StringComparison.OrdinalIgnoreCase))
        {
            return "title";
        }

        if (blob.Contains(" film", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("movie", StringComparison.OrdinalIgnoreCase))
        {
            return "film";
        }

        return string.IsNullOrWhiteSpace(kind) ? "other" : kind;
    }

    /// <summary>
    /// Replaces model text with a Wikipedia article. No article, a sequel miss, or a cue word
    /// the article never mentions becomes a reject.
    /// </summary>
    public static async Task<(List<AiEntityMention> Mentions, List<AiVerifyDecision> Rejects)> GroundAsync(
        IReadOnlyList<AiEntityMention> mentions,
        IReadOnlyList<AiVerifyDecision> decisions,
        Func<string, CancellationToken, Task<EntityLookupResult>> lookup,
        CancellationToken cancellationToken)
    {
        var cues = new Dictionary<long, string>();
        foreach (var decision in decisions)
        {
            if (!string.IsNullOrWhiteSpace(decision.CueText))
            {
                cues.TryAdd(decision.StartMs, decision.CueText);
            }
        }

        var kept = new List<AiEntityMention>();
        var rejects = new List<AiVerifyDecision>();
        foreach (var mention in mentions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cues.TryGetValue(mention.StartMs, out var cue);
            EntityLookupResult? hit = null;
            foreach (var title in LookupTitles(mention.Term, cue))
            {
                var result = await lookup(title, cancellationToken).ConfigureAwait(false);
                if (result.Found && !string.IsNullOrWhiteSpace(result.Summary))
                {
                    hit = result;
                    break;
                }
            }

            if (hit is null)
            {
                rejects.Add(Reject(mention, cue, "No Wikipedia article.", "not-found"));
                continue;
            }

            var mismatch = ArticleMismatch(mention.Term, cue, hit.Title, hit.Summary);
            if (mismatch is not null)
            {
                rejects.Add(Reject(mention, cue, mismatch, "namesake"));
                continue;
            }

            mention.Term = string.IsNullOrWhiteSpace(hit.Title) ? mention.Term : hit.Title.Trim();
            mention.Summary = PopupSummary(mention.Term, hit.Summary);
            mention.Kind = KindFromArticle(mention.Kind, mention.Term, hit.Summary);
            mention.Url = hit.Url;
            mention.ImageUrl = hit.ImageUrl;
            kept.Add(mention);
        }

        return (kept, rejects);
    }

    private static AiVerifyDecision Reject(AiEntityMention mention, string? cue, string reason, string category)
        => new()
        {
            Term = mention.Term,
            StartMs = mention.StartMs,
            CueText = cue,
            Kept = false,
            Reason = reason,
            Category = category,
            AtUtc = DateTime.UtcNow
        };

    /// <summary>
    /// First sentence of a Wikipedia extract, capped for a popup.
    /// </summary>
    public static string PopupSummary(string title, string extract)
    {
        var summary = (extract ?? string.Empty).Trim();
        var sentenceEnd = summary.IndexOf(". ", StringComparison.Ordinal);
        if (sentenceEnd > 40 && sentenceEnd < 280)
        {
            summary = summary[..(sentenceEnd + 1)];
        }
        else if (summary.Length > 280)
        {
            summary = summary[..277].TrimEnd() + "...";
        }

        if (summary.Length == 0)
        {
            return title;
        }

        return summary.StartsWith(title, StringComparison.OrdinalIgnoreCase)
            ? summary
            : title + ": " + summary;
    }

    private static bool TermContains(string longer, string shorter)
    {
        var longParts = Words(longer);
        var shortParts = Words(shorter);
        if (shortParts.Count == 0 || shortParts.Count > longParts.Count)
        {
            return false;
        }

        for (var i = 0; i <= longParts.Count - shortParts.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < shortParts.Count; j++)
            {
                if (!longParts[i + j].Equals(shortParts[j], StringComparison.OrdinalIgnoreCase))
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SharesTitleTail(string shorter, string longer)
    {
        var shortWords = Words(shorter);
        var longWords = Words(longer);
        if (shortWords.Count == 0 || longWords.Count <= shortWords.Count)
        {
            return false;
        }

        return longWords[0].Equals(shortWords[^1], StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLeadingFragment(string shorter, string longer, string cue)
    {
        var idx = cue.IndexOf(shorter, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var after = cue[(idx + shorter.Length)..];
        var gap = LeadingGapRegex().Match(after);
        if (!gap.Success)
        {
            return false;
        }

        var rest = after[gap.Length..];
        return rest.StartsWith(longer, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SequelMarker(string term, string? cue)
    {
        if (string.IsNullOrWhiteSpace(cue) || string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        var pattern = Regex.Escape(term.Trim()) + @"\s+(?<mark>[2-4]|II|III|IV)\b";
        var match = Regex.Match(cue, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["mark"].Value : null;
    }

    private static bool TitleHasSequelMarker(string title, string marker)
    {
        var roman = ToRoman(marker);
        if (!string.IsNullOrEmpty(roman)
            && Regex.IsMatch(title, @"\b" + roman + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(title, @"\b" + Regex.Escape(marker) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ToRoman(string marker)
    {
        return marker.Trim().ToUpperInvariant() switch
        {
            "2" or "II" => "II",
            "3" or "III" => "III",
            "4" or "IV" => "IV",
            _ => marker.Trim()
        };
    }

    private static List<string> CueDomainTokens(string term, string? cue)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(cue))
        {
            return found;
        }

        var tokens = WordTokenRegex().Matches(cue).Select(m => m.Value).ToList();
        var ordered = Words(term);
        var termWords = new HashSet<string>(ordered, StringComparer.OrdinalIgnoreCase);
        var termAt = -1;
        if (ordered.Count > 0)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Equals(ordered[0], StringComparison.OrdinalIgnoreCase))
                {
                    termAt = i;
                    break;
                }
            }
        }

        if (termAt < 0)
        {
            return found;
        }

        var from = Math.Max(0, termAt - 5);
        var to = Math.Min(tokens.Count - 1, termAt + termWords.Count + 4);
        for (var i = from; i <= to; i++)
        {
            var token = tokens[i];
            if (termWords.Contains(token) || token.Length < 5 || DialogueWords.Contains(token))
            {
                continue;
            }

            if (char.IsUpper(token[0]))
            {
                continue;
            }

            if (!found.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(token);
            }
        }

        return found;
    }

    private static List<string> Words(string term)
    {
        return term.Split([' ', '\'', '’'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim('.', ',', '"', '“', '”'))
            .Where(w => w.Length > 1 && !w.Equals("the", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string StripTermPrefix(string term, string summary)
    {
        var s = (summary ?? string.Empty).Trim();
        if (s.StartsWith(term, StringComparison.OrdinalIgnoreCase))
        {
            s = s[term.Length..].TrimStart(':', ' ', '-', '—');
        }

        return s;
    }

    [GeneratedRegex(@"^(?:['’]s)?[\s""“”]+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingGapRegex();

    [GeneratedRegex(@"[\p{L}][\p{L}'’\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();
}
