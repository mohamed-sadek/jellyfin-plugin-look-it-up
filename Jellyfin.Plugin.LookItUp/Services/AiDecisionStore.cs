using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Merges AI verify decisions into cache for debugging and retry of transient failures.
/// </summary>
public static class AiDecisionStore
{
    private const int MaxDecisions = 1000;

    /// <summary>
    /// Appends or updates decisions on the cache when storage is enabled.
    /// Retryable failures (HTTP 429) are always stored so catch-up can re-verify them.
    /// </summary>
    public static void Merge(ItemAnnotationCache cache, IEnumerable<AiVerifyDecision> incoming, bool enabled)
    {
        cache.AiDecisions ??= [];
        foreach (var decision in incoming)
        {
            if (!enabled && !IsRetryableFailure(decision))
            {
                continue;
            }

            var idx = cache.AiDecisions.FindIndex(d =>
                d.Term.Equals(decision.Term, StringComparison.OrdinalIgnoreCase)
                && d.StartMs == decision.StartMs);
            if (idx >= 0)
            {
                cache.AiDecisions[idx] = decision;
            }
            else
            {
                cache.AiDecisions.Add(decision);
            }
        }

        if (cache.AiDecisions.Count > MaxDecisions)
        {
            cache.AiDecisions = cache.AiDecisions
                .OrderByDescending(d => d.AtUtc)
                .Take(MaxDecisions)
                .OrderBy(d => d.StartMs)
                .ToList();
        }
    }

    /// <summary>
    /// True when a prior decision was a transient failure (HTTP 429 / transport) that should be retried.
    /// </summary>
    public static bool IsRetryableFailure(AiVerifyDecision? decision)
    {
        if (decision is null || decision.Kept || string.IsNullOrWhiteSpace(decision.Term))
        {
            return false;
        }

        if (string.Equals(decision.Category, "error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var reason = decision.Reason ?? string.Empty;
        return reason.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("rate-limited", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("rate limited", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the cache still has retryable verify failures for terms not yet annotated.
    /// </summary>
    public static bool HasRetryableFailures(ItemAnnotationCache? cache)
    {
        if (cache?.AiDecisions is null || cache.AiDecisions.Count == 0)
        {
            return false;
        }

        var known = new HashSet<string>(
            cache.Annotations.Select(a => a.Term),
            StringComparer.OrdinalIgnoreCase);

        return cache.AiDecisions.Any(d => IsRetryableFailure(d) && !known.Contains(d.Term));
    }

    /// <summary>
    /// Terms the AI kept (these are the only summaries that should be shown when AI is configured).
    /// </summary>
    public static HashSet<string> GetKeptTerms(IEnumerable<AiVerifyDecision>? decisions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (decisions is null)
        {
            return set;
        }

        foreach (var decision in decisions)
        {
            if (decision.Kept && !string.IsNullOrWhiteSpace(decision.Term))
            {
                set.Add(decision.Term);
            }
        }

        return set;
    }

    /// <summary>
    /// True when cache popups were filled without an AI keep (legacy Wikipedia summaries).
    /// </summary>
    public static bool HasSummariesWithoutAiKeep(ItemAnnotationCache? cache)
    {
        if (cache?.Annotations is null || cache.Annotations.Count == 0)
        {
            return false;
        }

        var kept = GetKeptTerms(cache.AiDecisions);
        return cache.Annotations.Any(a => !kept.Contains(a.Term));
    }

    /// <summary>
    /// Drops Wikipedia/heuristic popups so AI can rewrite them. Resets the prepare cursor.
    /// </summary>
    /// <returns>How many annotations were removed.</returns>
    public static int DiscardSummariesWithoutAiKeep(ItemAnnotationCache cache)
    {
        cache.AiDecisions ??= [];
        cache.Annotations ??= [];
        if (cache.Annotations.Count == 0)
        {
            return 0;
        }

        var kept = GetKeptTerms(cache.AiDecisions);
        var before = cache.Annotations.Count;
        cache.Annotations = cache.Annotations.Where(a => kept.Contains(a.Term)).ToList();
        var removed = before - cache.Annotations.Count;
        if (removed > 0)
        {
            cache.FullyPrepared = false;
            cache.PreparedThroughMs = 0;
        }

        return removed;
    }

    /// <summary>
    /// Terms that were deliberately rejected (not transient errors) and should not be re-verified.
    /// </summary>
    public static HashSet<string> GetSettledRejectTerms(IEnumerable<AiVerifyDecision>? decisions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (decisions is null)
        {
            return set;
        }

        foreach (var decision in decisions)
        {
            if (decision.Kept || IsRetryableFailure(decision) || string.IsNullOrWhiteSpace(decision.Term))
            {
                continue;
            }

            set.Add(decision.Term);
        }

        return set;
    }

    /// <summary>
    /// Retryable failure decisions for terms that are not already annotated.
    /// </summary>
    public static IReadOnlyList<AiVerifyDecision> GetRetryableFailures(
        IEnumerable<AiVerifyDecision>? decisions,
        IEnumerable<ContextAnnotation> existing)
    {
        if (decisions is null)
        {
            return [];
        }

        var known = new HashSet<string>(
            existing.Select(a => a.Term),
            StringComparer.OrdinalIgnoreCase);

        return decisions
            .Where(d => IsRetryableFailure(d) && !known.Contains(d.Term))
            .GroupBy(d => d.Term, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.AtUtc).First())
            .OrderBy(d => d.StartMs)
            .ToList();
    }
}
