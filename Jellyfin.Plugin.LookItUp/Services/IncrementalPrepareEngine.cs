using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LookItUp.Configuration;
using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Incremental prepare: local/cloud model reads subtitle lines and writes popup definitions.
/// </summary>
public sealed class IncrementalPrepareEngine
{
    private readonly ISubtitleParser _subtitleParser;
    private readonly IAiEntityExtractor _ai;
    private readonly IReferenceGate _gate;
    private readonly ILogger<IncrementalPrepareEngine> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrementalPrepareEngine"/> class.
    /// </summary>
    public IncrementalPrepareEngine(
        ISubtitleParser subtitleParser,
        IAiEntityExtractor ai,
        IReferenceGate gate,
        ILogger<IncrementalPrepareEngine> logger)
    {
        _subtitleParser = subtitleParser;
        _ai = ai;
        _gate = gate;
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
            return (EmptyWindow(fromMs, toMs), "skipped", null);
        }

        if (!_ai.IsConfigured(config))
        {
            if (toMs > cache.PreparedThroughMs)
            {
                cache.PreparedThroughMs = toMs;
            }

            return (EmptyWindow(fromMs, toMs), "model-missing",
                "Set Provider to Groq and add an API key, or Ollama on a machine you control.");
        }

        var windowCues = cues.Where(c => c.StartMs >= fromMs && c.StartMs < toMs).ToList();
        var beforeCount = cache.Annotations.Count;
        var known = cache.Annotations.Select(a => a.Term).ToList();
        var extracted = await _ai
            .ExtractCuesAsync(mediaContext, windowCues, known, config, cancellationToken)
            .ConfigureAwait(false);
        var popupMs = Math.Max(config.PopupDurationMs, 8000);
        var max = Math.Max(1, config.MaxAnnotationsPerItem);
        var verified = new List<string>();

        foreach (var mention in extracted.Mentions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new NameCandidate
            {
                Term = mention.Term,
                StartMs = mention.StartMs,
                EndMs = mention.EndMs,
                CueText = windowCues.FirstOrDefault(c => c.StartMs == mention.StartMs)?.Text ?? string.Empty
            };
            var local = _gate.TryRejectLocal(candidate, mediaContext.ShowName, excludeCast);
            if (local is not null)
            {
                AiDecisionStore.Merge(cache, [PhraseReferencePipeline.ToStoreDecision(local)], enabled: true);
                continue;
            }

            var decision = new AiVerifyDecision
            {
                Term = mention.Term,
                StartMs = mention.StartMs,
                CueText = candidate.CueText,
                Kept = true,
                Reason = "model-batch",
                Category = mention.Kind,
                AtUtc = DateTime.UtcNow
            };
            AiDecisionStore.Merge(cache, [decision], enabled: true);
            var summary = mention.Summary.Trim();
            var term = mention.Term.Trim();
            MergeAnnotations(
                cache,
                [
                    new ContextAnnotation
                    {
                        Term = term,
                        Summary = summary.StartsWith(term, StringComparison.OrdinalIgnoreCase)
                            ? summary
                            : $"{term}: {summary}",
                        Kind = string.IsNullOrWhiteSpace(mention.Kind) ? "other" : mention.Kind,
                        StartMs = mention.StartMs,
                        EndMs = Math.Max(mention.EndMs, mention.StartMs + popupMs)
                    }
                ],
                max);
            verified.Add(term);
            persistCache?.Invoke();
        }

        if (!string.IsNullOrWhiteSpace(extracted.Warning))
        {
            AiDecisionStore.Merge(
                cache,
                [
                    new AiVerifyDecision
                    {
                        Term = "(window)",
                        StartMs = fromMs,
                        Kept = false,
                        Reason = extracted.Warning,
                        Category = "error",
                        AtUtc = DateTime.UtcNow
                    }
                ],
                enabled: true);
        }

        if (toMs > cache.PreparedThroughMs)
        {
            cache.PreparedThroughMs = toMs;
        }

        cache.ScannedAtUtc = DateTime.UtcNow;
        var window = new IncrementalPrepareWindowResult
        {
            FromMs = fromMs,
            ToMs = toMs,
            CandidatesInWindow = windowCues.Count,
            CandidatesVerified = verified.Count,
            AnnotationsAdded = cache.Annotations.Count - beforeCount,
            SkippedTerms = [],
            VerifiedTerms = verified
        };
        _logger.LogInformation(
            "Incremental window {From}-{To}ms: cues={Cues} added={Added} total={Total} mode=model",
            fromMs,
            toMs,
            windowCues.Count,
            window.AnnotationsAdded,
            cache.Annotations.Count);
        return (window, "model", extracted.Warning);
    }

    /// <summary>
    /// Runs incremental windows from 0 → subtitle end.
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
                Cache = BuildEmptyCache(request, 0),
                Mode = "model",
                Warning = "No subtitle cues parsed.",
                SubtitleDurationMs = 0
            };
        }

        var durationMs = cues.Max(c => c.EndMs);
        var excludeCast = new HashSet<string>(request.ExcludeCastNames, StringComparer.OrdinalIgnoreCase);
        LookItUpService.AddSubtitleSpeakerNames(excludeCast, cues, Math.Max(2, config.MinEntityLength));
        var cache = BuildEmptyCache(request, durationMs);
        cache.SubtitleHash = ComputeSubtitleHash(request.SubtitleContent);
        var mediaContext = new AiMediaContext
        {
            ShowName = request.ShowName,
            EpisodeName = request.EpisodeName,
            KnownCastNames = excludeCast.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(100).ToList()
        };

        if (request.DryRun)
        {
            cache.PreparedThroughMs = durationMs;
            cache.FullyPrepared = true;
            return new IncrementalPrepareSimulationResult
            {
                Cache = cache,
                Windows =
                [
                    new IncrementalPrepareWindowResult
                    {
                        FromMs = 0,
                        ToMs = durationMs,
                        CandidatesInWindow = cues.Count,
                        VerifiedTerms = cues.Select(c => c.Text).Take(8).ToList()
                    }
                ],
                SubtitleDurationMs = durationMs,
                Mode = "dry-run",
                Warning = _ai.IsConfigured(config)
                    ? $"Would send {cues.Count} cues to the model in {Math.Max(1, (int)Math.Ceiling(durationMs / (double)request.WindowMs))} windows."
                    : "No model configured (set Provider to Groq and add an API key)."
            };
        }

        var windows = new List<IncrementalPrepareWindowResult>();
        string? warning = null;
        var mode = "model";
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
            warning ??= windowWarning;
            windows.Add(window);
        }

        cache.FullyPrepared = cache.PreparedThroughMs >= durationMs
                              && !AiDecisionStore.HasRetryableFailures(cache);
        cache.PrepareOutcome = cache.Annotations.Count > 0 ? "success" : "no-candidates";
        return new IncrementalPrepareSimulationResult
        {
            Cache = cache,
            Windows = windows,
            SubtitleDurationMs = durationMs,
            Mode = mode,
            Warning = warning
        };
    }

    private static IncrementalPrepareWindowResult EmptyWindow(long fromMs, long toMs)
        => new() { FromMs = fromMs, ToMs = toMs };

    private static ItemAnnotationCache BuildEmptyCache(IncrementalPrepareRequest request, long durationMs)
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

    private static void MergeAnnotations(
        ItemAnnotationCache cache,
        IReadOnlyList<ContextAnnotation> incoming,
        int maxTotal)
    {
        cache.Annotations = cache.Annotations
            .Concat(incoming)
            .GroupBy(a => a.Term, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(a => a.StartMs).First())
            .OrderBy(a => a.StartMs)
            .Take(maxTotal)
            .ToList();
    }
}
