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
    /// Tie-break or rewrite one Wikimedia decision. Never drops a confident Wikimedia keep on AI failure.
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

        if (decision.Uncertain && budget.TieBreaksLeft > 0)
        {
            budget.TieBreaksLeft--;
            var tie = await _ai
                .TieBreakAsync(media, decision, config, cancellationToken)
                .ConfigureAwait(false);
            if (!tie.Ok)
            {
                if (!decision.Kept
                    && !string.IsNullOrWhiteSpace(tie.Error)
                    && (tie.Error.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase)
                        || tie.Error.Contains("rate-limited", StringComparison.OrdinalIgnoreCase)))
                {
                    decision.Category = "error";
                    decision.Reason = tie.Error;
                }

                _logger.LogInformation(
                    "Look it up Groq tie-break skipped for {Term}: {Error}",
                    decision.Candidate.Term,
                    tie.Error ?? "failed");
                return decision;
            }

            if (tie.Keep)
            {
                await ApplyKeepFromTieAsync(decision, tie, wikipediaLanguage, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (decision.Uncertain)
            {
                decision.Kept = false;
                decision.Category = string.IsNullOrWhiteSpace(tie.Category) ? "groq-tiebreak" : tie.Category;
                decision.Reason = string.IsNullOrWhiteSpace(tie.Reason)
                    ? "Groq rejected an uncertain Wikimedia pick."
                    : tie.Reason;
            }

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
    public async Task<IReadOnlyList<ContextAnnotation>> SweepLeftoverCuesAsync(
        IReadOnlyList<SubtitleCue> windowCues,
        IReadOnlyList<NameCandidate> windowCandidates,
        IReadOnlyCollection<string> alreadyKnown,
        AiMediaContext media,
        PluginConfiguration config,
        AiComplementBudget budget,
        int popupMs,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled(config) || budget.IdiomCallsLeft <= 0 || windowCues.Count == 0)
        {
            return [];
        }

        var leftover = windowCues
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .Where(c => !windowCandidates.Any(n =>
                !string.IsNullOrWhiteSpace(n.Term)
                && c.Text.Contains(n.Term, StringComparison.OrdinalIgnoreCase)))
            .Take(12)
            .ToList();
        if (leftover.Count == 0)
        {
            return [];
        }

        budget.IdiomCallsLeft--;
        var sweep = await _ai
            .SweepIdiomsAsync(media, leftover, alreadyKnown, config, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(sweep.Error))
        {
            _logger.LogInformation("Look it up idiom sweep skipped: {Error}", sweep.Error);
            return [];
        }

        var added = new List<ContextAnnotation>();
        var known = new HashSet<string>(alreadyKnown, StringComparer.OrdinalIgnoreCase);
        foreach (var mention in sweep.Mentions)
        {
            if (string.IsNullOrWhiteSpace(mention.Term) || !known.Add(mention.Term))
            {
                continue;
            }

            string? imageUrl = null;
            string? url = null;
            if (string.Equals(mention.Kind, "person", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var wiki = await _wikipedia
                        .LookupAsync(mention.Term, WikipediaLanguageOrEn(config), cancellationToken)
                        .ConfigureAwait(false);
                    if (wiki.Found)
                    {
                        imageUrl = wiki.ImageUrl;
                        url = wiki.Url;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Idiom person image lookup failed for {Term}", mention.Term);
                }
            }

            var title = mention.Term.Trim();
            var summary = mention.Summary.Trim();
            added.Add(new ContextAnnotation
            {
                Term = title,
                Summary = summary.StartsWith(title, StringComparison.OrdinalIgnoreCase)
                    ? summary
                    : $"{title}: {summary}",
                Url = url,
                ImageUrl = imageUrl,
                Kind = string.IsNullOrWhiteSpace(mention.Kind) ? "other" : mention.Kind,
                StartMs = mention.StartMs,
                EndMs = Math.Max(mention.EndMs, mention.StartMs + Math.Max(8000, popupMs))
            });
        }

        return added;
    }

    private async Task ApplyKeepFromTieAsync(
        ReferenceDecision decision,
        AiTieBreakResult tie,
        string wikipediaLanguage,
        CancellationToken cancellationToken)
    {
        var title = string.IsNullOrWhiteSpace(tie.Title) ? (decision.Title ?? decision.Candidate.Term) : tie.Title.Trim();
        var summary = string.IsNullOrWhiteSpace(tie.Summary) ? decision.Summary : tie.Summary;
        if (!string.IsNullOrWhiteSpace(title)
            && !title.Equals(decision.Title, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var wiki = await _wikipedia.LookupAsync(title, wikipediaLanguage, cancellationToken)
                    .ConfigureAwait(false);
                if (wiki.Found)
                {
                    decision.Url = wiki.Url ?? decision.Url;
                    decision.ImageUrl = wiki.ImageUrl ?? decision.ImageUrl;
                    if (!string.IsNullOrWhiteSpace(wiki.Title))
                    {
                        title = wiki.Title.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(summary) && !string.IsNullOrWhiteSpace(wiki.Summary))
                    {
                        summary = wiki.Summary;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tie-break Wikipedia lookup failed for {Title}", title);
            }
        }

        if (string.Equals(tie.Kind, "person", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(decision.ImageUrl))
        {
            try
            {
                var wiki = await _wikipedia.LookupAsync(title, wikipediaLanguage, cancellationToken)
                    .ConfigureAwait(false);
                if (wiki.Found)
                {
                    decision.ImageUrl = wiki.ImageUrl ?? decision.ImageUrl;
                    decision.Url = wiki.Url ?? decision.Url;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tie-break person image lookup failed for {Title}", title);
            }
        }

        decision.Kept = true;
        decision.Title = title;
        decision.Kind = string.IsNullOrWhiteSpace(tie.Kind) ? decision.Kind ?? "other" : tie.Kind;
        decision.Category = "groq-tiebreak";
        decision.Reason = string.IsNullOrWhiteSpace(tie.Reason) ? "Groq tie-break" : tie.Reason;
        decision.Uncertain = false;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            decision.Summary = summary.StartsWith(title, StringComparison.OrdinalIgnoreCase)
                ? summary
                : $"{title}: {summary}";
        }
    }

    private static string WikipediaLanguageOrEn(PluginConfiguration config)
        => string.IsNullOrWhiteSpace(config.WikipediaLanguage) ? "en" : config.WikipediaLanguage.Trim();
}
