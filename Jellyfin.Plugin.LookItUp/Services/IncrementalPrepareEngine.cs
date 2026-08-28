using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LookItUp.Configuration;
using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Simulates incremental playback prepare over a subtitle timeline in fixed windows.
/// Uses the same name finding, Wikimedia (default) or optional AI verification, and annotation shaping as full prepare.
/// </summary>
public sealed class IncrementalPrepareEngine
{
    private readonly ISubtitleParser _subtitleParser;
    private readonly INameCandidateFinder _nameCandidateFinder;
    private readonly IWikipediaLookupService _wikipedia;
    private readonly IWikimediaReferencePipeline _wikimedia;
    private readonly IAiComplementService _complement;
    private readonly ILogger<IncrementalPrepareEngine> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrementalPrepareEngine"/> class.
    /// </summary>
    public IncrementalPrepareEngine(
        ISubtitleParser subtitleParser,
        INameCandidateFinder nameCandidateFinder,
        IWikipediaLookupService wikipedia,
        IWikimediaReferencePipeline wikimedia,
        IAiComplementService complement,
        ILogger<IncrementalPrepareEngine> logger)
    {
        _subtitleParser = subtitleParser;
        _nameCandidateFinder = nameCandidateFinder;
        _wikipedia = wikipedia;
        _wikimedia = wikimedia;
        _complement = complement;
        _logger = logger;
    }

    /// <summary>
    /// Prepares one incremental window during playback and merges into the item cache.
    /// </summary>
    public async Task<(IncrementalPrepareWindowResult Window, string Mode, string? Warning)> PrepareWindowAsync(
        ItemAnnotationCache cache,
        IReadOnlyList<SubtitleCue> cues,
        string itemTitle,
        IReadOnlySet<string> excludeCast,
        AiMediaContext mediaContext,
        long fromMs,
        long toMs,
        PluginConfiguration config,
        CancellationToken cancellationToken,
        bool retriesOnly = false,
        Action? persistCache = null)
    {
        if ((!retriesOnly && fromMs >= toMs) || cues.Count == 0)
        {
            return (new IncrementalPrepareWindowResult
            {
                FromMs = fromMs,
                ToMs = toMs
            }, "skipped", null);
        }

        if (retriesOnly && !AiDecisionStore.HasRetryableFailures(cache))
        {
            return (new IncrementalPrepareWindowResult
            {
                FromMs = fromMs,
                ToMs = toMs
            }, "skipped", null);
        }

        var max = Math.Max(1, config.MaxAnnotationsPerItem);
        var minLen = Math.Max(2, config.MinEntityLength);
        const int prepareCandidateCap = 750;
        var ranked = _nameCandidateFinder
            .Find(cues, itemTitle, excludeCast, minLen, prepareCandidateCap)
            .ToList();

        var skipped = GetSkippedTerms(ranked, fromMs, toMs, cache.Annotations);
        var beforeCount = cache.Annotations.Count;
        string? warning = null;
        var groqOn = _complement.IsEnabled(config);
        var mode = groqOn ? "wikimedia+ai" : "wikimedia";
        var windowLimit = Math.Clamp(config.IncrementalAiNamesPerWindow, 5, 250);

        var windowCandidates = NameCandidateBatchSelector
            .SelectForWindow(
                ranked,
                fromMs,
                toMs,
                cache.Annotations,
                windowLimit,
                cache.AiDecisions,
                retriesOnly);

        var wikiLang = string.IsNullOrWhiteSpace(config.WikipediaLanguage) ? "en" : config.WikipediaLanguage;
        var popupMs = Math.Max(config.PopupDurationMs, 8000);
        var budget = groqOn ? AiComplementBudget.ForWindow() : new AiComplementBudget();
        var windowCues = cues.Where(c => c.StartMs >= fromMs && c.StartMs < toMs).ToList();
        foreach (var candidate in windowCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await _wikimedia
                .EvaluateAsync(candidate, mediaContext.ShowName, excludeCast, wikiLang, cancellationToken)
                .ConfigureAwait(false);
            if (groqOn)
            {
                decision = await _complement
                    .ApplyToDecisionAsync(decision, mediaContext, config, budget, wikiLang, cancellationToken)
                    .ConfigureAwait(false);
            }

            AiDecisionStore.Merge(
                cache,
                [WikimediaReferencePipeline.ToStoreDecision(decision)],
                enabled: true);
            var annotation = WikimediaReferencePipeline.ToAnnotation(decision, popupMs);
            if (annotation is not null)
            {
                MergeAnnotations(cache, [annotation], max);
            }

            persistCache?.Invoke();
        }

        if (groqOn)
        {
            var known = cache.Annotations
                .Select(a => a.Term)
                .Concat(windowCandidates.Select(c => c.Term))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var idioms = await _complement
                .SweepLeftoverCuesAsync(
                    windowCues,
                    windowCandidates,
                    known,
                    mediaContext,
                    config,
                    budget,
                    popupMs,
                    cancellationToken)
                .ConfigureAwait(false);
            if (idioms.Count > 0)
            {
                MergeAnnotations(cache, idioms, max);
                AiDecisionStore.Merge(
                    cache,
                    idioms.Select(a => new AiVerifyDecision
                    {
                        Term = a.Term,
                        StartMs = a.StartMs,
                        CueText = windowCues.FirstOrDefault(c => c.StartMs == a.StartMs)?.Text,
                        Kept = true,
                        Reason = "groq-idiom",
                        Category = "groq-idiom",
                        AtUtc = DateTime.UtcNow
                    }),
                    enabled: true);
                persistCache?.Invoke();
            }
        }

        // Only advance the timeline cursor when this was a forward window (not a catch-up retry).
        if (toMs > cache.PreparedThroughMs)
        {
            cache.PreparedThroughMs = toMs;
        }

        cache.ScannedAtUtc = DateTime.UtcNow;

        var window = new IncrementalPrepareWindowResult
        {
            FromMs = fromMs,
            ToMs = toMs,
            CandidatesInWindow = CountCandidatesInWindow(ranked, fromMs, toMs),
            CandidatesVerified = windowCandidates.Count,
            AnnotationsAdded = cache.Annotations.Count - beforeCount,
            SkippedTerms = skipped,
            VerifiedTerms = windowCandidates.Select(c => c.Term).ToList()
        };

        _logger.LogInformation(
            "Incremental window {From}-{To}ms: verified={Verified} added={Added} total={Total} mode={Mode}",
            fromMs,
            toMs,
            windowCandidates.Count,
            window.AnnotationsAdded,
            cache.Annotations.Count,
            mode);

        return (window, mode, warning);
    }

    /// <summary>
    /// Runs incremental 5-minute-style windows from 0 → subtitle end and merges annotations into cache.
    /// </summary>
    public async Task<IncrementalPrepareSimulationResult> SimulateAsync(
        IncrementalPrepareRequest request,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var cues = _subtitleParser.Parse(request.SubtitleContent, request.SubtitleFileName);
        if (cues.Count == 0)
        {
            return new IncrementalPrepareSimulationResult
            {
                Cache = BuildEmptyCache(request, config, durationMs: 0),
                Mode = request.DryRun ? "dry-run" : (_complement.IsEnabled(config) ? "wikimedia+ai" : "wikimedia"),
                Warning = "No subtitle cues parsed.",
                SubtitleDurationMs = 0
            };
        }

        var durationMs = cues.Max(c => c.EndMs);
        var excludeCast = new HashSet<string>(request.ExcludeCastNames, StringComparer.OrdinalIgnoreCase);
        var minLen = Math.Max(2, config.MinEntityLength);
        // Find() also harvests speakers; seed exclude cast for AI hints the same way.
        const int prepareCandidateCap = 750;
        var ranked = _nameCandidateFinder
            .Find(cues, request.ItemTitle, excludeCast, minLen, prepareCandidateCap)
            .ToList();
        // Re-sync exclude set with whatever Find harvested (speakers).
        // Find clones exclude internally, so harvest again for AI KnownCastNames.
        LookItUpService.AddSubtitleSpeakerNames(excludeCast, cues, minLen);

        var cache = BuildEmptyCache(request, config, durationMs);
        cache.SubtitleHash = ComputeSubtitleHash(request.SubtitleContent);
        cache.DurationCheckOk = true;

        var windows = new List<IncrementalPrepareWindowResult>();
        var mode = request.DryRun ? "dry-run" : (_complement.IsEnabled(config) ? "wikimedia+ai" : "wikimedia");
        string? warning = null;
        var windowLimit = Math.Clamp(config.IncrementalAiNamesPerWindow, 5, 250);

        if (request.DryRun)
        {
            for (var fromMs = 0L; fromMs < durationMs; fromMs += request.WindowMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toMs = Math.Min(fromMs + request.WindowMs, durationMs);
                var windowCandidates = NameCandidateBatchSelector
                    .SelectForWindow(ranked, fromMs, toMs, cache.Annotations, windowLimit, cache.AiDecisions);
                var skipped = GetSkippedTerms(ranked, fromMs, toMs, cache.Annotations);

                windows.Add(new IncrementalPrepareWindowResult
                {
                    FromMs = fromMs,
                    ToMs = toMs,
                    CandidatesInWindow = CountCandidatesInWindow(ranked, fromMs, toMs),
                    CandidatesVerified = windowCandidates.Count,
                    AnnotationsAdded = 0,
                    SkippedTerms = skipped,
                    VerifiedTerms = windowCandidates.Select(c => c.Term).ToList()
                });

                cache.PreparedThroughMs = toMs;
            }

            cache.FullyPrepared = cache.PreparedThroughMs >= durationMs
                                  && !AiDecisionStore.HasRetryableFailures(cache);
            cache.PrepareOutcome = ranked.Count == 0 ? "no-candidates" : "success";
            return new IncrementalPrepareSimulationResult
            {
                Cache = cache,
                Windows = windows,
                SubtitleDurationMs = durationMs,
                Mode = mode,
                Warning = ranked.Count == 0 ? "No local name candidates found." : null
            };
        }

        var mediaContext = new AiMediaContext
        {
            ShowName = request.ShowName,
            EpisodeName = request.EpisodeName,
            KnownCastNames = excludeCast.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(100).ToList()
        };

        for (var fromMs = 0L; fromMs < durationMs; fromMs += request.WindowMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toMs = Math.Min(fromMs + request.WindowMs, durationMs);
            var (window, windowMode, windowWarning) = await PrepareWindowAsync(
                    cache,
                    cues,
                    request.ItemTitle,
                    excludeCast,
                    mediaContext,
                    fromMs,
                    toMs,
                    config,
                    cancellationToken)
                .ConfigureAwait(false);
            mode = windowMode;
            if (string.IsNullOrWhiteSpace(warning) && !string.IsNullOrWhiteSpace(windowWarning))
            {
                warning = windowWarning;
            }

            windows.Add(window);
        }

        cache.FullyPrepared = cache.PreparedThroughMs >= durationMs
                              && !AiDecisionStore.HasRetryableFailures(cache);
        cache.PrepareOutcome = cache.Annotations.Count > 0
            ? "success"
            : ranked.Count == 0 ? "no-candidates" : "success";

        return new IncrementalPrepareSimulationResult
        {
            Cache = cache,
            Windows = windows,
            SubtitleDurationMs = durationMs,
            Mode = mode,
            Warning = warning ?? (ranked.Count == 0 ? "No local name candidates found." : null)
        };
    }

    private static ItemAnnotationCache BuildEmptyCache(
        IncrementalPrepareRequest request,
        PluginConfiguration config,
        long durationMs)
    {
        return new ItemAnnotationCache
        {
            ItemId = request.ItemId,
            Version = LookItUpService.CurrentCacheVersion,
            ScannedAtUtc = DateTime.UtcNow,
            SubtitlePath = request.SubtitleFileName,
            SubtitleSource = "simulator",
            MatchedBy = "simulator",
            DurationCheckOk = durationMs > 0,
            PrepareOutcome = "success",
            Disabled = false,
            Annotations = [],
            PreparedThroughMs = 0,
            FullyPrepared = false
        };
    }

    private static string ComputeSubtitleHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static int CountCandidatesInWindow(
        IReadOnlyList<NameCandidate> ranked,
        long fromMs,
        long toMs)
        => ranked.Count(c => c.StartMs >= fromMs && c.StartMs < toMs);

    private static IReadOnlyList<string> GetSkippedTerms(
        IReadOnlyList<NameCandidate> ranked,
        long fromMs,
        long toMs,
        IReadOnlyList<ContextAnnotation> existing)
    {
        var known = new HashSet<string>(
            existing.Select(a => a.Term),
            StringComparer.OrdinalIgnoreCase);

        return ranked
            .Where(c => c.StartMs >= fromMs && c.StartMs < toMs)
            .Select(c => c.Term)
            .Where(known.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void MergeAnnotations(
        ItemAnnotationCache cache,
        IReadOnlyList<ContextAnnotation> incoming,
        int maxTotal)
    {
        if (incoming.Count == 0)
        {
            return;
        }

        var merged = cache.Annotations
            .Concat(incoming)
            .GroupBy(a => a.Term, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(a => a.StartMs).First())
            .OrderBy(a => a.StartMs)
            .Take(maxTotal)
            .ToList();

        cache.Annotations = merged;
    }

    private async Task<List<ContextAnnotation>> BuildAnnotationsFromAiAsync(
        IReadOnlyList<AiEntityMention> mentions,
        IReadOnlyList<SubtitleCue> cues,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var popupMs = Math.Max(config.PopupDurationMs, 8000);
        var wikiLang = string.IsNullOrWhiteSpace(config.WikipediaLanguage) ? "en" : config.WikipediaLanguage;
        var built = new List<ContextAnnotation>();

        foreach (var m in mentions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var term = m.Term.Trim();
            if (OpenAiCompatibleEntityExtractor.IsSongOrMusicWork(term, m.Kind, m.Summary))
            {
                continue;
            }

            var kind = string.IsNullOrWhiteSpace(m.Kind)
                       || string.Equals(m.Kind, "other", StringComparison.OrdinalIgnoreCase)
                ? InferKind(term, m.Summary)
                : m.Kind.Trim().ToLowerInvariant();
            if (string.Equals(m.Kind, "person", StringComparison.OrdinalIgnoreCase))
            {
                kind = "person";
            }

            if (string.Equals(kind, "song", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var anchored = FindEarliestMention(cues, term);
            var startMs = anchored?.StartMs ?? m.StartMs;
            var endMs = anchored?.EndMs ?? m.EndMs;

            string? pageUrl = null;
            string? imageUrl = null;
            if (string.Equals(kind, "person", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var wiki = await _wikipedia.LookupAsync(term, wikiLang, cancellationToken).ConfigureAwait(false);
                    if (wiki.Found)
                    {
                        pageUrl = wiki.Url;
                        imageUrl = wiki.ImageUrl;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Wikipedia image lookup failed for {Term}", term);
                }
            }

            built.Add(new ContextAnnotation
            {
                Term = term,
                Summary = m.Summary.Trim().StartsWith(term, StringComparison.OrdinalIgnoreCase)
                    ? m.Summary.Trim()
                    : $"{term}: {m.Summary.Trim()}",
                Url = pageUrl,
                ImageUrl = imageUrl,
                Kind = kind,
                StartMs = startMs,
                EndMs = Math.Max(endMs, startMs + popupMs)
            });
        }

        return built;
    }

    private static string InferKind(string term, string summary)
    {
        var text = (term + " " + summary).ToLowerInvariant();
        if (text.Contains("actor", StringComparison.Ordinal)
            || text.Contains("actress", StringComparison.Ordinal)
            || text.Contains("singer", StringComparison.Ordinal)
            || text.Contains("musician", StringComparison.Ordinal)
            || text.Contains("politician", StringComparison.Ordinal)
            || text.Contains("athlete", StringComparison.Ordinal)
            || text.Contains("director", StringComparison.Ordinal)
            || text.Contains("comedian", StringComparison.Ordinal)
            || text.Contains("writer", StringComparison.Ordinal)
            || text.Contains("author", StringComparison.Ordinal))
        {
            return "person";
        }

        if (text.Contains("film", StringComparison.Ordinal)
            || text.Contains("movie", StringComparison.Ordinal)
            || text.Contains("television", StringComparison.Ordinal))
        {
            return "film";
        }

        return "other";
    }

    private static (long StartMs, long EndMs)? FindEarliestMention(
        IReadOnlyList<SubtitleCue> cues,
        string term)
    {
        if (string.IsNullOrWhiteSpace(term) || cues.Count == 0)
        {
            return null;
        }

        var needle = term.Trim();
        foreach (var cue in cues.OrderBy(c => c.StartMs))
        {
            if (CueContainsTerm(cue.Text, needle))
            {
                return (cue.StartMs, cue.EndMs);
            }
        }

        return null;
    }

    private static bool CueContainsTerm(string? cueText, string term)
    {
        if (string.IsNullOrWhiteSpace(cueText))
        {
            return false;
        }

        var text = cueText;
        var idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var beforeOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            var afterIndex = idx + term.Length;
            var afterOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (beforeOk && (afterOk || text[afterIndex] is '\'' or '’' or ',' or '.' or '!' or '?' or ';' or ':'))
            {
                return true;
            }

            idx = text.IndexOf(term, idx + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
