using Jellyfin.Plugin.LookItUp.Models;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Keep/drop rules for Wikimedia-resolved subtitle names.
/// </summary>
public interface IReferenceGate
{
    /// <summary>
    /// Returns a drop decision when the term can be rejected without a network lookup.
    /// </summary>
    ReferenceDecision? TryRejectLocal(
        NameCandidate candidate,
        string? showName,
        IReadOnlySet<string> excludeCast);

    /// <summary>
    /// Applies Wikidata type, in-show, and obviousness rules to a resolved hit.
    /// </summary>
    ReferenceDecision Decide(
        NameCandidate candidate,
        WikimediaReferenceHit hit,
        string? showName,
        IReadOnlySet<string> excludeCast);
}

/// <summary>
/// Wikipedia search + Wikidata P31 + summary lookup (no API key).
/// </summary>
public interface IWikimediaReferenceResolver
{
    /// <summary>
    /// Resolves a candidate against Wikipedia search and Wikidata.
    /// </summary>
    Task<WikimediaReferenceHit> ResolveAsync(
        string term,
        string? cueText,
        string language,
        CancellationToken cancellationToken);
}

/// <summary>
/// Finder-facing Wikimedia gate used by prepare and the local CLI.
/// </summary>
public interface IWikimediaReferencePipeline
{
    /// <summary>
    /// Evaluates one name candidate (local filters, then Wikimedia).
    /// </summary>
    Task<ReferenceDecision> EvaluateAsync(
        NameCandidate candidate,
        string? showName,
        IReadOnlySet<string> excludeCast,
        string language,
        CancellationToken cancellationToken);
}
