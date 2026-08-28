using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Turns raw Wikidata entities into an unambiguous phrase → Wikipedia title map.
/// </summary>
public static class PhraseIndexCompiler
{
    /// <summary>Minimum sitelinks for a one-word human alias (Mussolini, not Donna).</summary>
    public const int MinOneWordHumanSitelinks = 60;

    /// <summary>
    /// Compiles unique phrases. Short ambiguous aliases are dropped; exact Wikipedia titles win.
    /// </summary>
    public static IReadOnlyList<PhraseIndexEntry> Compile(IEnumerable<PhraseIndexSourceEntity> entities)
    {
        var claims = new Dictionary<string, List<Claim>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            if (!TryNormalizeEntity(entity, out var qid, out var title, out var kind, out var types, out var sitelinks))
            {
                continue;
            }

            if (types.Count > 0 && types.All(CulturalSkipList.GivenNameTypes.Contains))
            {
                continue;
            }

            var isHuman = types.Any(CulturalSkipList.HumanTypes.Contains);
            var isWork = types.Any(CulturalSkipList.WorkTypes.Contains);
            var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            phrases.Add(title);
            foreach (var raw in entity.Phrases ?? [])
            {
                if (TryNormalizePhrase(raw, out var phrase))
                {
                    phrases.Add(phrase);
                }
            }

            foreach (var phrase in phrases)
            {
                if (CulturalSkipList.IsObvious(phrase))
                {
                    continue;
                }

                var tokens = CountTokens(phrase);
                if (tokens == 1 && CulturalSkipList.CommonOneWord.Contains(phrase))
                {
                    continue;
                }

                if (tokens == 1 && isWork)
                {
                    continue;
                }

                if (tokens == 1 && isHuman && sitelinks < MinOneWordHumanSitelinks)
                {
                    continue;
                }

                if (tokens == 1 && isHuman && types.Any(CulturalSkipList.GivenNameTypes.Contains))
                {
                    continue;
                }

                if (!claims.TryGetValue(phrase, out var list))
                {
                    list = [];
                    claims[phrase] = list;
                }

                list.Add(new Claim(
                    qid,
                    title,
                    kind,
                    sitelinks,
                    phrase.Equals(title, StringComparison.OrdinalIgnoreCase)));
            }
        }

        var entries = new List<PhraseIndexEntry>(claims.Count);
        foreach (var (phrase, list) in claims)
        {
            var winner = PickWinner(phrase, list);
            if (winner is null)
            {
                continue;
            }

            entries.Add(new PhraseIndexEntry
            {
                Phrase = phrase.ToLowerInvariant(),
                Title = winner.Title,
                Qid = winner.Qid,
                Kind = winner.Kind
            });
        }

        return entries
            .OrderBy(e => e.Phrase, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Claim? PickWinner(string phrase, List<Claim> list)
    {
        if (list.Count == 0)
        {
            return null;
        }

        var distinct = list
            .GroupBy(c => c.Qid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.ExactTitle).ThenByDescending(c => c.Sitelinks).First())
            .ToList();

        var exact = distinct.Where(c => c.ExactTitle).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        if (exact.Count > 1)
        {
            return Dominant(exact);
        }

        var tokens = CountTokens(phrase);
        if (distinct.Count == 1)
        {
            return distinct[0];
        }

        if (tokens <= 2)
        {
            return Dominant(distinct);
        }

        return Dominant(distinct);
    }

    private static Claim? Dominant(IReadOnlyList<Claim> distinct)
    {
        var ranked = distinct.OrderByDescending(c => c.Sitelinks).ToList();
        if (ranked.Count == 1)
        {
            return ranked[0];
        }

        var top = ranked[0];
        var second = ranked[1];
        if (top.Sitelinks >= Math.Max(20, second.Sitelinks * 2))
        {
            return top;
        }

        return null;
    }

    private static bool TryNormalizeEntity(
        PhraseIndexSourceEntity entity,
        out string qid,
        out string title,
        out string kind,
        out List<string> types,
        out int sitelinks)
    {
        qid = (entity.Qid ?? string.Empty).Trim();
        title = (entity.Title ?? string.Empty).Trim();
        kind = "other";
        types = entity.Types ?? [];
        sitelinks = entity.Sitelinks;
        if (string.IsNullOrWhiteSpace(qid) || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (title.Contains("disambiguation", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("List of ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        kind = KindFromTypes(types);
        return TryNormalizePhrase(title, out title);
    }

    /// <summary>Maps Wikidata P31 ids to a popup kind.</summary>
    public static string KindFromTypes(IReadOnlyList<string> types)
    {
        if (types.Any(CulturalSkipList.HumanTypes.Contains))
        {
            return "person";
        }

        if (types.Any(t => t is "Q11424" or "Q24862" or "Q202866" or "Q5398426" or "Q15416" or "Q1261214"))
        {
            return "film";
        }

        if (types.Any(t => t is "Q3231690" or "Q1420" or "Q431289" or "Q167270" or "Q7868205"
                or "Q4830453" or "Q783794" or "Q6881511" or "Q59773381" or "Q20799151" or "Q18524218"
                or "Q507619" or "Q161726"))
        {
            return "brand";
        }

        if (types.Any(t => t is "Q41176" or "Q811979" or "Q187456" or "Q12280" or "Q3918"
                or "Q46831" or "Q34763" or "Q23442"))
        {
            return "place";
        }

        return "other";
    }

    /// <summary>Normalizes a label into a searchable phrase.</summary>
    public static bool TryNormalizePhrase(string? raw, out string phrase)
    {
        phrase = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        var paren = trimmed.IndexOf('(');
        if (paren > 3)
        {
            trimmed = trimmed[..paren].Trim();
        }

        trimmed = trimmed.Replace('_', ' ');
        while (trimmed.Contains("  ", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (trimmed.Length < 3 || trimmed.Length > 80)
        {
            return false;
        }

        if (trimmed.Any(ch => ch is '/' or '{' or '}' or '[' or ']' or '<' or '>' or '|'))
        {
            return false;
        }

        phrase = trimmed;
        return true;
    }

    /// <summary>Counts whitespace-separated tokens.</summary>
    public static int CountTokens(string phrase)
        => phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private sealed record Claim(string Qid, string Title, string Kind, int Sitelinks, bool ExactTitle);
}
