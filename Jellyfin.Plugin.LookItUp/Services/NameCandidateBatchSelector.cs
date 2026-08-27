using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Picks which name candidates to send to AI verification.
/// </summary>
public static class NameCandidateBatchSelector
{
    /// <summary>
    /// Selects candidates in a playback window, preferring high-score and shorter earlier forms.
    /// Retries prior HTTP 429 / transport failures, and skips settled rejects.
    /// </summary>
    /// <param name="ranked">Score-ranked name candidates for the item.</param>
    /// <param name="fromMs">Window start (ms).</param>
    /// <param name="toMs">Window end (ms), exclusive.</param>
    /// <param name="existing">Annotations already kept.</param>
    /// <param name="limit">Max candidates to return.</param>
    /// <param name="priorDecisions">Prior AI decisions (for retries / settled rejects).</param>
    /// <param name="retriesOnly">When true, only return prior retryable failures (FullyPrepared catch-up).</param>
    public static List<NameCandidate> SelectForWindow(
        IReadOnlyList<NameCandidate> ranked,
        long fromMs,
        long toMs,
        IReadOnlyList<ContextAnnotation> existing,
        int limit,
        IReadOnlyList<AiVerifyDecision>? priorDecisions = null,
        bool retriesOnly = false)
    {
        var known = new HashSet<string>(
            existing.Select(a => a.Term),
            StringComparer.OrdinalIgnoreCase);
        var settledRejects = AiDecisionStore.GetSettledRejectTerms(priorDecisions);

        var retries = BuildRetryCandidates(ranked, priorDecisions, existing, limit);
        if (retriesOnly)
        {
            return retries.Take(limit).OrderBy(c => c.StartMs).ToList();
        }

        var retrySlots = Math.Min(retries.Count, Math.Max(1, limit / 2));
        if (retries.Count > 0 && limit <= 5)
        {
            retrySlots = Math.Min(retries.Count, limit);
        }

        var batch = new List<NameCandidate>(limit);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var retry in retries.Take(retrySlots))
        {
            if (used.Add(retry.Term))
            {
                batch.Add(retry);
            }
        }

        var inWindow = ranked
            .Where(c => c.StartMs >= fromMs && c.StartMs < toMs)
            .Where(c => !known.Contains(c.Term))
            .Where(c => !settledRejects.Contains(c.Term))
            .Where(c => !used.Contains(c.Term))
            .GroupBy(c => c.Term, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Score).ThenBy(c => c.StartMs).First())
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.StartMs)
            .ToList();

        var remaining = Math.Max(0, limit - batch.Count);
        if (remaining > 0)
        {
            foreach (var candidate in SelectAiBatch(inWindow, remaining))
            {
                if (used.Add(candidate.Term))
                {
                    batch.Add(candidate);
                }
            }
        }

        // If the window was empty but retries remain, fill with retries.
        if (batch.Count < limit)
        {
            foreach (var retry in retries)
            {
                if (batch.Count >= limit)
                {
                    break;
                }

                if (used.Add(retry.Term))
                {
                    batch.Add(retry);
                }
            }
        }

        return batch.OrderBy(c => c.StartMs).ToList();
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

    private static List<NameCandidate> BuildRetryCandidates(
        IReadOnlyList<NameCandidate> ranked,
        IReadOnlyList<AiVerifyDecision>? priorDecisions,
        IReadOnlyList<ContextAnnotation> existing,
        int limit)
    {
        var failures = AiDecisionStore.GetRetryableFailures(priorDecisions, existing);
        if (failures.Count == 0)
        {
            return [];
        }

        var byTerm = ranked
            .GroupBy(c => c.Term, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Score).ThenBy(c => c.StartMs).First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<NameCandidate>(Math.Min(failures.Count, limit));
        foreach (var failure in failures)
        {
            if (byTerm.TryGetValue(failure.Term, out var rankedMatch))
            {
                result.Add(rankedMatch);
                continue;
            }

            result.Add(new NameCandidate
            {
                Term = failure.Term,
                StartMs = failure.StartMs,
                EndMs = failure.StartMs + 3000,
                CueText = failure.CueText ?? string.Empty,
                Score = 40,
                Reason = "retry-error"
            });
        }

        return result;
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
