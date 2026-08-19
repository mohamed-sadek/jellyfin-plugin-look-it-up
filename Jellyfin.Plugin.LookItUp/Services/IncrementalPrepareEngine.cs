using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LookItUp.Configuration;
using Jellyfin.Plugin.LookItUp.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Simulates incremental playback prepare over a subtitle timeline in fixed windows.
/// Uses the same name finding, AI verification, and annotation shaping as full prepare.
/// </summary>
public sealed class IncrementalPrepareEngine
{
    private readonly ISubtitleParser _subtitleParser;
    private readonly INameCandidateFinder _nameCandidateFinder;
    private readonly IEntityExtractor _entityExtractor;
    private readonly IWikipediaLookupService _wikipedia;
    private readonly IAiEntityExtractor _aiExtractor;
    private readonly ILogger<IncrementalPrepareEngine> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrementalPrepareEngine"/> class.
    /// </summary>
    public IncrementalPrepareEngine(
        ISubtitleParser subtitleParser,
        INameCandidateFinder nameCandidateFinder,
        IEntityExtractor entityExtractor,
        IWikipediaLookupService wikipedia,
        IAiEntityExtractor aiExtractor,
        ILogger<IncrementalPrepareEngine> logger)
    {
        _subtitleParser = subtitleParser;
        _nameCandidateFinder = nameCandidateFinder;
        _entityExtractor = entityExtractor;
        _wikipedia = wikipedia;
        _aiExtractor = aiExtractor;
        _logger = logger;
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
                Mode = request.DryRun ? "dry-run" : (_aiExtractor.IsConfigured(config) ? "ai" : "legacy"),
                Warning = "No subtitle cues parsed.",
                SubtitleDurationMs = 0
            };
        }

        var durationMs = cues.Max(c => c.EndMs);
        var max = Math.Max(1, config.MaxAnnotationsPerItem);
        var excludeCast = new HashSet<string>(request.ExcludeCastNames, StringComparer.OrdinalIgnoreCase);
        var minLen = Math.Max(2, config.MinEntityLength);
        const int prepareCandidateCap = 500;
        var ranked = _nameCandidateFinder
            .Find(cues, request.ItemTitle, excludeCast, minLen, prepareCandidateCap)
            .ToList();

        var cache = BuildEmptyCache(request, config, durationMs);
        cache.SubtitleHash = ComputeSubtitleHash(request.SubtitleContent);
        cache.DurationCheckOk = true;

        var windows = new List<IncrementalPrepareWindowResult>();
        var mode = request.DryRun ? "dry-run" : (_aiExtractor.IsConfigured(config) ? "ai" : "legacy");
        string? warning = null;

        if (request.DryRun)
        {
            for (var fromMs = 0L; fromMs < durationMs; fromMs += request.WindowMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toMs = Math.Min(fromMs + request.WindowMs, durationMs);
                var windowCandidates = SelectWindowCandidates(ranked, fromMs, toMs, cache.Annotations);
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

            cache.FullyPrepared = cache.PreparedThroughMs >= durationMs;
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

        if (_aiExtractor.IsConfigured(config))
        {
            var nameLimit = config.AiNamesPerPrepare <= 0
                ? 200
                : Math.Clamp(config.AiNamesPerPrepare, 1, 200);
            var mediaContext = new AiMediaContext
            {
                ShowName = request.ShowName,
                EpisodeName = request.EpisodeName,
                KnownCastNames = excludeCast.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(40).ToList()
            };

            for (var fromMs = 0L; fromMs < durationMs; fromMs += request.WindowMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toMs = Math.Min(fromMs + request.WindowMs, durationMs);
                var windowCandidates = SelectWindowCandidates(ranked, fromMs, toMs, cache.Annotations)
                    .Take(nameLimit)
                    .ToList();
                var skipped = GetSkippedTerms(ranked, fromMs, toMs, cache.Annotations);
                var beforeCount = cache.Annotations.Count;

                if (windowCandidates.Count > 0)
                {
                    var aiResult = await _aiExtractor
                        .ResolveNamesAsync(mediaContext, windowCandidates, config, cancellationToken)
                        .ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(warning) && !string.IsNullOrWhiteSpace(aiResult.Warning))
                    {
                        warning = aiResult.Warning;
                    }

                    var added = await BuildAnnotationsFromAiAsync(
                            aiResult.Mentions,
                            cues,
                            config,
                            cancellationToken)
                        .ConfigureAwait(false);

                    MergeAnnotations(cache, added, max);
                }

                cache.PreparedThroughMs = toMs;
                cache.ScannedAtUtc = DateTime.UtcNow;

                windows.Add(new IncrementalPrepareWindowResult
                {
                    FromMs = fromMs,
                    ToMs = toMs,
                    CandidatesInWindow = CountCandidatesInWindow(ranked, fromMs, toMs),
                    CandidatesVerified = windowCandidates.Count,
                    AnnotationsAdded = cache.Annotations.Count - beforeCount,
                    SkippedTerms = skipped,
                    VerifiedTerms = windowCandidates.Select(c => c.Term).ToList()
                });

                _logger.LogInformation(
                    "Incremental window {From}-{To}ms: verified={Verified} added={Added} total={Total}",
                    fromMs,
                    toMs,
                    windowCandidates.Count,
                    cache.Annotations.Count - beforeCount,
                    cache.Annotations.Count);
            }
        }
        else
        {
            mode = "legacy";
            var popupMs = Math.Max(config.PopupDurationMs, 8000);
            var usedTerms = new HashSet<string>(
                cache.Annotations.Select(a => a.Term),
                StringComparer.OrdinalIgnoreCase);

            for (var fromMs = 0L; fromMs < durationMs; fromMs += request.WindowMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toMs = Math.Min(fromMs + request.WindowMs, durationMs);
                var windowCues = cues.Where(c => c.StartMs >= fromMs && c.StartMs < toMs).ToList();
                var beforeCount = cache.Annotations.Count;
                var verifiedTerms = new List<string>();

                foreach (var cue in windowCues)
                {
                    if (cache.Annotations.Count >= max)
                    {
                        break;
                    }

                    IReadOnlyList<string> entities;
                    try
                    {
                        entities = _entityExtractor.Extract(cue.Text, config.MinEntityLength);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Entity extract failed for cue at {StartMs}", cue.StartMs);
                        continue;
                    }

                    foreach (var entity in entities)
                    {
                        if (!usedTerms.Add(entity))
                        {
                            continue;
                        }

                        verifiedTerms.Add(entity);
                        var lookup = await _wikipedia
                            .LookupAsync(entity, config.WikipediaLanguage, cancellationToken)
                            .ConfigureAwait(false);
                        if (!lookup.Found)
                        {
                            continue;
                        }

                        MergeAnnotations(
                            cache,
                            [
                                new ContextAnnotation
                                {
                                    Term = lookup.Title,
                                    Summary = $"{lookup.Title}: {lookup.Summary}",
                                    Url = lookup.Url,
                                    ImageUrl = lookup.ImageUrl,
                                    Kind = "other",
                                    StartMs = cue.StartMs,
                                    EndMs = Math.Max(cue.EndMs, cue.StartMs + popupMs)
                                }
                            ],
                            max);
                    }
                }

                cache.PreparedThroughMs = toMs;
                cache.ScannedAtUtc = DateTime.UtcNow;

                windows.Add(new IncrementalPrepareWindowResult
                {
                    FromMs = fromMs,
                    ToMs = toMs,
                    CandidatesInWindow = windowCues.Count,
                    CandidatesVerified = verifiedTerms.Count,
                    AnnotationsAdded = cache.Annotations.Count - beforeCount,
                    SkippedTerms = [],
                    VerifiedTerms = verifiedTerms
                });
            }
        }

        cache.FullyPrepared = cache.PreparedThroughMs >= durationMs;
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

    private static List<NameCandidate> SelectWindowCandidates(
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
            .Where(c => !known.Contains(c.Term))
            .GroupBy(c => c.Term, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(c => c.StartMs).First())
            .OrderBy(c => c.StartMs)
            .ToList();
    }

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
