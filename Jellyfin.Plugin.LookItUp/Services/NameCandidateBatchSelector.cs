using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Picks which name candidates to send to AI verification.
/// </summary>
public static class NameCandidateBatchSelector
{
    /// <summary>
    /// Selects candidates in a playback window, preferring high-score and shorter earlier forms.
    /// </summary>
    public static List<NameCandidate> SelectForWindow(
        IReadOnlyList<NameCandidate> ranked,
        long fromMs,
        long toMs,
        IReadOnlyList<ContextAnnotation> existing,
        int limit)
    {
        var known = new HashSet<string>(
            existing.Select(a => a.Term),
            StringComparer.OrdinalIgnoreCase);

        var inWindow = ranked
            .Where(c => c.StartMs >= fromMs && c.StartMs < toMs)
            .Where(c => !known.Contains(c.Term))
            .GroupBy(c => c.Term, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Score).ThenBy(c => c.StartMs).First())
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.StartMs)
            .ToList();

        return SelectAiBatch(inWindow, limit);
    }

    /// <summary>
    /// Picks AI verify targets, preferring earlier shorter Cap+Cap forms over later long phrases
    /// (e.g. "Jon Voight" @ 0:59 over "Jon Voight's LeBaron" @ 2:31).
    /// </summary>
    public static List<NameCandidate> SelectAiBatch(IReadOnlyList<NameCandidate> ranked, int limit)
    {
        var batch = new List<NameCandidate>(limit);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in ranked)
        {
            if (batch.Count >= limit)
            {
                break;
            }

            var preferred = PreferEarlierShorterForm(candidate, ranked);
            if (used.Add(preferred.Term))
            {
                batch.Add(preferred);
            }

            if (batch.Count >= limit)
            {
                break;
            }

            var tail = GetPossessiveTail(candidate.Term);
            if (tail is null || !used.Add(tail))
            {
                continue;
            }

            NameCandidate? tailCandidate = null;
            foreach (var other in ranked)
            {
                if (!other.Term.Equals(tail, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (tailCandidate is null || other.StartMs < tailCandidate.StartMs)
                {
                    tailCandidate = other;
                }
            }

            batch.Add(tailCandidate ?? new NameCandidate
            {
                Term = tail,
                StartMs = candidate.StartMs,
                EndMs = candidate.EndMs,
                CueText = candidate.CueText,
                Score = candidate.Score,
                Reason = "possessive-tail"
            });
        }

        return batch.OrderBy(c => c.StartMs).ToList();
    }

    private static NameCandidate PreferEarlierShorterForm(
        NameCandidate candidate,
        IReadOnlyList<NameCandidate> pool)
    {
        var parts = candidate.Term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return candidate;
        }

        var head2 = parts[0] + " " + StripPossessive(parts[1]);
        NameCandidate? best = null;
        foreach (var other in pool)
        {
            if (!other.Term.Equals(head2, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (other.StartMs <= candidate.StartMs && (best is null || other.StartMs < best.StartMs))
            {
                best = other;
            }
        }

        return best ?? candidate;
    }

    private static string? GetPossessiveTail(string term)
    {
        var parts = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!IsPossessiveToken(parts[i]))
            {
                continue;
            }

            var tail = string.Join(' ', parts.Skip(i + 1));
            return string.IsNullOrWhiteSpace(tail) ? null : tail;
        }

        return null;
    }

    private static bool IsPossessiveToken(string token)
    {
        return token.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
               || token.EndsWith("’s", StringComparison.OrdinalIgnoreCase)
               || token.EndsWith("'", StringComparison.Ordinal)
               || token.EndsWith("’", StringComparison.Ordinal);
    }

    private static string StripPossessive(string token)
    {
        if (token.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
            || token.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
        {
            return token[..^2];
        }

        if (token.EndsWith("'", StringComparison.Ordinal) || token.EndsWith("’", StringComparison.Ordinal))
        {
            return token[..^1];
        }

        return token;
    }
}
