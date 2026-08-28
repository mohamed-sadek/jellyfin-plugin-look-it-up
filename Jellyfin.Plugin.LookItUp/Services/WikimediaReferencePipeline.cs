using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Local filters then Wikimedia resolve + type gate.
/// </summary>
public sealed class WikimediaReferencePipeline : IWikimediaReferencePipeline
{
    private readonly IWikimediaReferenceResolver _resolver;
    private readonly IReferenceGate _gate;
    private readonly ILogger<WikimediaReferencePipeline> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WikimediaReferencePipeline"/> class.
    /// </summary>
    public WikimediaReferencePipeline(
        IWikimediaReferenceResolver resolver,
        IReferenceGate gate,
        ILogger<WikimediaReferencePipeline> logger)
    {
        _resolver = resolver;
        _gate = gate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReferenceDecision> EvaluateAsync(
        NameCandidate candidate,
        string? showName,
        IReadOnlySet<string> excludeCast,
        string language,
        CancellationToken cancellationToken)
    {
        var local = _gate.TryRejectLocal(candidate, showName, excludeCast);
        if (local is not null)
        {
            return local;
        }

        var hit = await _resolver
            .ResolveAsync(candidate.Term, candidate.SearchContext ?? candidate.CueText, language, cancellationToken)
            .ConfigureAwait(false);
        var decision = _gate.Decide(candidate, hit, showName, excludeCast);
        _logger.LogInformation(
            "Look it up Wikimedia {Keep} {Term} → {Title}: [{Category}] {Reason}",
            decision.Kept ? "KEEP" : "DROP",
            candidate.Term,
            decision.Title ?? "-",
            decision.Category,
            decision.Reason);
        return decision;
    }

    /// <summary>
    /// Converts a gate decision into the sidecar audit record.
    /// </summary>
    public static AiVerifyDecision ToStoreDecision(ReferenceDecision decision)
    {
        return new AiVerifyDecision
        {
            Term = decision.Candidate.Term,
            StartMs = decision.Candidate.StartMs,
            CueText = decision.Candidate.CueText,
            Kept = decision.Kept,
            Reason = decision.Reason,
            Category = decision.Category,
            AtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Converts a kept decision into a timed popup annotation.
    /// </summary>
    public static ContextAnnotation? ToAnnotation(ReferenceDecision decision, int popupMs)
    {
        if (!decision.Kept)
        {
            return null;
        }

        var term = string.IsNullOrWhiteSpace(decision.Title) ? decision.Candidate.Term : decision.Title.Trim();
        var summary = string.IsNullOrWhiteSpace(decision.Summary) ? term : decision.Summary.Trim();
        var startMs = decision.Candidate.StartMs;
        var endMs = decision.Candidate.EndMs;
        return new ContextAnnotation
        {
            Term = term,
            Summary = summary,
            Url = decision.Url,
            ImageUrl = decision.ImageUrl,
            Kind = decision.Kind ?? "other",
            StartMs = startMs,
            EndMs = Math.Max(endMs, startMs + Math.Max(8000, popupMs))
        };
    }
}
