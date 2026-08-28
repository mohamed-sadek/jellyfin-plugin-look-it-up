using Jellyfin.Plugin.LookItUp.Configuration;
using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Call budget so Groq cannot starve a window (TPM / 429).
/// </summary>
public sealed class AiComplementBudget
{
    /// <summary>Gets or sets remaining tie-break calls.</summary>
    public int TieBreaksLeft { get; set; }

    /// <summary>Gets or sets remaining popup-rewrite calls.</summary>
    public int RewritesLeft { get; set; }

    /// <summary>Gets or sets remaining leftover-cue idiom sweeps.</summary>
    public int IdiomCallsLeft { get; set; }

    /// <summary>Window-sized budget (incremental prepare).</summary>
    public static AiComplementBudget ForWindow() => new()
    {
        TieBreaksLeft = 4,
        RewritesLeft = 4,
        IdiomCallsLeft = 1
    };

    /// <summary>Full-item budget (library prepare).</summary>
    public static AiComplementBudget ForFullPrepare() => new()
    {
        TieBreaksLeft = 16,
        RewritesLeft = 12,
        IdiomCallsLeft = 2
    };
}

/// <summary>
/// Groq/OpenAI second pass on top of Wikimedia keep/drop.
/// </summary>
public interface IAiComplementService
{
    /// <summary>True when a chat provider is configured.</summary>
    bool IsEnabled(PluginConfiguration config);

    /// <summary>
    /// Rewrite a confident Wikimedia keep, or drop/switch among real Wikipedia pages when the hit is ambiguous.
    /// Never promotes a not-found drop into a keep.
    /// </summary>
    Task<ReferenceDecision> ApplyToDecisionAsync(
        ReferenceDecision decision,
        AiMediaContext media,
        PluginConfiguration config,
        AiComplementBudget budget,
        string wikipediaLanguage,
        CancellationToken cancellationToken);

    /// <summary>
    /// One small pass over leftover cues that produced no Cap candidates.
    /// </summary>
    Task<IReadOnlyList<ContextAnnotation>> SweepLeftoverCuesAsync(
        IReadOnlyList<SubtitleCue> windowCues,
        IReadOnlyList<NameCandidate> windowCandidates,
        IReadOnlyCollection<string> alreadyKnown,
        AiMediaContext media,
        PluginConfiguration config,
        AiComplementBudget budget,
        int popupMs,
        CancellationToken cancellationToken);
}

/// <summary>
/// Wikimedia-first Groq complement: ambiguous pages, popup rewrite, leftover idioms.
/// </summary>
public sealed class AiComplementService : IAiComplementService
{
    private readonly IAiEntityExtractor _ai;
    private readonly IWikipediaLookupService _wikipedia;
    private readonly ILogger<AiComplementService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiComplementService"/> class.
    /// </summary>
    public AiComplementService(
        IAiEntityExtractor ai,
        IWikipediaLookupService wikipedia,
        ILogger<AiComplementService> logger)
    {
        _ai = ai;
        _wikipedia = wikipedia;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration config) => _ai.IsConfigured(config);

    /// <inheritdoc />
    public async Task<ReferenceDecision> ApplyToDecisionAsync(
        ReferenceDecision decision,
        AiMediaContext media,
        PluginConfiguration config,
        AiComplementBudget budget,
        string wikipediaLanguage,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled(config))
        {
            return decision;
        }

        if (decision.Kept && budget.RewritesLeft > 0)
        {
            budget.RewritesLeft--;
            var rewritten = await _ai
                .RewritePopupAsync(
                    decision.Title ?? decision.Candidate.Term,
                    decision.Kind ?? "other",
                    decision.Candidate.CueText,
                    decision.Summary ?? string.Empty,
                    config,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(rewritten))
            {
                var title = decision.Title ?? decision.Candidate.Term;
                decision.Summary = rewritten.StartsWith(title, StringComparison.OrdinalIgnoreCase)
                    ? rewritten
                    : $"{title}: {rewritten}";
                decision.Reason = "groq-rewrite: " + decision.Reason;
            }
        }

        return decision;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContextAnnotation>> SweepLeftoverCuesAsync(
        IReadOnlyList<SubtitleCue> windowCues,
        IReadOnlyList<NameCandidate> windowCandidates,
        IReadOnlyCollection<string> alreadyKnown,
        AiMediaContext media,
        PluginConfiguration config,
        AiComplementBudget budget,
        int popupMs,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ContextAnnotation>>([]);
    }
}
