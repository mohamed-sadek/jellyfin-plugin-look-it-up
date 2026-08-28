using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Local skip list then exact Wikipedia summary for a phrase-index hit.
/// </summary>
public interface IPhraseReferencePipeline
{
    /// <summary>Evaluates one phrase match.</summary>
    Task<ReferenceDecision> EvaluateAsync(
        PhraseMatch match,
        string? showName,
        IReadOnlySet<string> excludeCast,
        string language,
        CancellationToken cancellationToken);
}

/// <summary>
/// Wikipedia REST summary by known title. No search, no P31 at runtime.
/// </summary>
public sealed class PhraseReferencePipeline : IPhraseReferencePipeline
{
    private readonly IWikipediaLookupService _wikipedia;
    private readonly IReferenceGate _gate;
    private readonly ILogger<PhraseReferencePipeline> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PhraseReferencePipeline"/> class.
    /// </summary>
    public PhraseReferencePipeline(
        IWikipediaLookupService wikipedia,
        IReferenceGate gate,
        ILogger<PhraseReferencePipeline> logger)
    {
        _wikipedia = wikipedia;
        _gate = gate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReferenceDecision> EvaluateAsync(
        PhraseMatch match,
        string? showName,
        IReadOnlySet<string> excludeCast,
        string language,
        CancellationToken cancellationToken)
    {
        var candidate = ToCandidate(match);
        var local = _gate.TryRejectLocal(candidate, showName, excludeCast);
        if (local is not null)
        {
            return local;
        }

        if (CulturalSkipList.IsObvious(match.Title))
        {
            return Drop(candidate, "too-common", "Globally obvious Wikipedia title.");
        }

        var wikiLang = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        var lookup = await _wikipedia
            .LookupAsync(match.Title, wikiLang, cancellationToken)
            .ConfigureAwait(false);
        if (!lookup.Found || string.IsNullOrWhiteSpace(lookup.Title) || string.IsNullOrWhiteSpace(lookup.Summary))
        {
            var miss = Drop(candidate, "not-found", "No Wikipedia article for indexed title.");
            _logger.LogInformation("Look it up phrase DROP {Phrase} → {Title}: not-found", match.Phrase, match.Title);
            return miss;
        }

        if (lookup.Title.Contains("disambiguation", StringComparison.OrdinalIgnoreCase)
            || lookup.Title.StartsWith("List of ", StringComparison.OrdinalIgnoreCase))
        {
            return Drop(candidate, "wikidata-type", "Disambiguation or list article.");
        }

        var titleCheck = ToCandidate(match);
        titleCheck.Term = lookup.Title;
        var titleLocal = _gate.TryRejectLocal(titleCheck, showName, excludeCast);
        if (titleLocal is not null)
        {
            titleLocal.Candidate = candidate;
            return titleLocal;
        }

        var title = lookup.Title.Trim();
        var summary = lookup.Summary.Trim();
        var decision = new ReferenceDecision
        {
            Candidate = candidate,
            Kept = true,
            Category = match.Kind,
            Kind = match.Kind,
            Reason = "phrase-index",
            Title = title,
            Summary = summary.StartsWith(title, StringComparison.OrdinalIgnoreCase)
                ? summary
                : $"{title}: {summary}",
            Url = lookup.Url,
            ImageUrl = lookup.ImageUrl,
            WikidataId = match.Qid,
            Uncertain = false
        };
        _logger.LogInformation(
            "Look it up phrase KEEP {Phrase} → {Title}: [{Kind}]",
            match.Phrase,
            title,
            match.Kind);
        return decision;
    }

    /// <summary>Converts a gate decision into the sidecar audit record.</summary>
    public static AiVerifyDecision ToStoreDecision(ReferenceDecision decision)
    {
        return new AiVerifyDecision
        {
            Term = decision.Title ?? decision.Candidate.Term,
            StartMs = decision.Candidate.StartMs,
            CueText = decision.Candidate.CueText,
            Kept = decision.Kept,
            Reason = decision.Reason,
            Category = decision.Category,
            AtUtc = DateTime.UtcNow
        };
    }

    /// <summary>Converts a kept decision into a timed popup.</summary>
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

    private static NameCandidate ToCandidate(PhraseMatch match)
    {
        return new NameCandidate
        {
            Term = match.Phrase,
            StartMs = match.StartMs,
            EndMs = match.EndMs,
            CueText = match.CueText,
            Score = match.Phrase.Length,
            Reason = "phrase-index"
        };
    }

    private static ReferenceDecision Drop(NameCandidate candidate, string category, string reason)
    {
        return new ReferenceDecision
        {
            Candidate = candidate,
            Kept = false,
            Category = category,
            Reason = reason,
            Title = candidate.Term,
            Uncertain = false
        };
    }
}
