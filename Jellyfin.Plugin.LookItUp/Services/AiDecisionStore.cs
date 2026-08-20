using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Merges AI verify decisions into cache for debugging.
/// </summary>
public static class AiDecisionStore
{
    private const int MaxDecisions = 1000;

    /// <summary>
    /// Appends or updates decisions on the cache when storage is enabled.
    /// </summary>
    public static void Merge(ItemAnnotationCache cache, IEnumerable<AiVerifyDecision> incoming, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        cache.AiDecisions ??= [];
        foreach (var decision in incoming)
        {
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
}
