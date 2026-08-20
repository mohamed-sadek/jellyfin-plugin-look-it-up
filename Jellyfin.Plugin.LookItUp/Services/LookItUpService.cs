using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LookItUp.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Orchestrates subtitle scanning and annotation generation.
/// </summary>
public interface ILookItUpService
{
    /// <summary>
    /// Current prepare/cache schema version.
    /// </summary>
    int CacheVersion { get; }

    /// <summary>
    /// Gets annotations for playback (cache-first; optional on-demand prepare).
    /// </summary>
    Task<IReadOnlyList<ContextAnnotation>> GetAnnotationsAsync(
        Guid itemId,
        bool forceRescan,
        CancellationToken cancellationToken);

    /// <summary>
    /// Precomputes and stores annotations for an item.
    /// </summary>
    /// <param name="itemId">Media item id.</param>
    /// <param name="force">When true, overwrite existing cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="selectedTerms">When set, only these terms are sent to AI (instead of automatic top-N).</param>
    Task<PrepareItemResult> PrepareItemAsync(
        Guid itemId,
        bool force,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? selectedTerms = null);

    /// <summary>
    /// Returns a prepared cache entry when present and current.
    /// </summary>
    bool TryGetPrepared(Guid itemId, out ItemAnnotationCache? cache);

    /// <summary>
    /// Dry-run: extract subtitle name candidates that would be sent to AI (no AI call).
    /// </summary>
    Task<NameCandidatesResult> GetNameCandidatesAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Builds a prepare preview for a series/season/episode/movie (no AI).
    /// </summary>
    Task<PreparePreviewResult> GetPreparePreviewAsync(
        Guid rootItemId,
        int? suggestedNamesPerItem,
        CancellationToken cancellationToken);

    /// <summary>
    /// Enables or disables popups for an item without deleting annotations.
    /// </summary>
    bool TrySetDisabled(Guid itemId, bool disabled, out ItemAnnotationCache? cache);

    /// <summary>
    /// Returns true when the cache counts as successfully prepared (skip-safe).
    /// </summary>
    bool IsSuccessfullyPrepared(ItemAnnotationCache cache);

    /// <summary>
    /// Incrementally prepares the next subtitle time window during playback.
    /// </summary>
    Task<PrepareAheadResult> PrepareAheadAsync(
        Guid itemId,
        long playbackMs,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes all generated Look it up files (plugin caches, downloaded subs, media sidecars, prepare queue).
    /// </summary>
    ClearGeneratedDataResult ClearGeneratedData();
}

/// <summary>
/// Builds timed Wikipedia annotations from a media item's subtitles
/// (external SRT/VTT or embedded text tracks).
/// </summary>
public class LookItUpService : ILookItUpService
{
    /// <summary>
    /// Bump when scan logic changes so stale caches are ignored.
    /// </summary>
    public const int CurrentCacheVersion = 10;

    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text", "text", "microdvd", "mpl2", "sami", "stl", "ttml", "dfxp"
    };

    private static readonly Regex SeasonEpisodeRegex = new(
        @"[Ss](?<s>\d{1,2})[Ee](?<e>\d{1,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ISubtitleParser _subtitleParser;
    private readonly IEntityExtractor _entityExtractor;
    private readonly IWikipediaLookupService _wikipedia;
    private readonly IAiEntityExtractor _aiExtractor;
    private readonly INameCandidateFinder _nameCandidateFinder;
    private readonly IOpenSubtitlesClient _openSubtitles;
    private readonly IAnnotationStore _store;
    private readonly IPrepareQueueStore _prepareQueueStore;
    private readonly IncrementalPrepareEngine _incrementalEngine;
    private readonly ILogger<LookItUpService> _logger;
    private readonly string _subtitleCacheDir;
    private readonly string _openSubtitlesDir;
    private readonly ConcurrentDictionary<Guid, SubtitleContent> _subtitleMemory = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _aheadLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LookItUpService"/> class.
    /// </summary>
    public LookItUpService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        ISubtitleParser subtitleParser,
        IEntityExtractor entityExtractor,
        IWikipediaLookupService wikipedia,
        IAiEntityExtractor aiExtractor,
        INameCandidateFinder nameCandidateFinder,
        IOpenSubtitlesClient openSubtitles,
        IAnnotationStore store,
        IPrepareQueueStore prepareQueueStore,
        IncrementalPrepareEngine incrementalEngine,
        IApplicationPaths appPaths,
        ILogger<LookItUpService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
        _subtitleParser = subtitleParser;
        _entityExtractor = entityExtractor;
        _wikipedia = wikipedia;
        _aiExtractor = aiExtractor;
        _nameCandidateFinder = nameCandidateFinder;
        _openSubtitles = openSubtitles;
        _store = store;
        _prepareQueueStore = prepareQueueStore;
        _incrementalEngine = incrementalEngine;
        _logger = logger;

        var root = appPaths.PluginConfigurationsPath
                   ?? appPaths.ProgramDataPath
                   ?? Path.GetTempPath();
        _subtitleCacheDir = Path.Combine(root, "LookItUp", "subtitles");
        _openSubtitlesDir = Path.Combine(root, "LookItUp", "opensubtitles");
        try
        {
            Directory.CreateDirectory(_subtitleCacheDir);
            Directory.CreateDirectory(_openSubtitlesDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create subtitle cache directory {Dir}", _subtitleCacheDir);
        }
    }

    /// <inheritdoc />
    public int CacheVersion => CurrentCacheVersion;

    /// <inheritdoc />
    public bool TryGetPrepared(Guid itemId, out ItemAnnotationCache? cache)
    {
        cache = _store.Get(itemId);
        if (cache is null || cache.Version < CurrentCacheVersion)
        {
            cache = null;
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public bool IsSuccessfullyPrepared(ItemAnnotationCache cache)
    {
        if (cache.Version < CurrentCacheVersion)
        {
            return false;
        }

        if (cache.Annotations.Count > 0)
        {
            return true;
        }

        // Empty-but-valid: no name candidates. Empty no-subtitles stays retryable.
        return string.Equals(cache.PrepareOutcome, "no-candidates", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool TrySetDisabled(Guid itemId, bool disabled, out ItemAnnotationCache? cache)
    {
        cache = _store.Get(itemId);
        if (cache is null)
        {
            return false;
        }

        cache.Disabled = disabled;
        _store.Save(cache);
        return true;
    }

    /// <inheritdoc />
    public ClearGeneratedDataResult ClearGeneratedData()
    {
        var result = new ClearGeneratedDataResult
        {
            CacheFilesDeleted = _store.ClearAll(),
            SubtitleCacheFilesDeleted = DeleteFilesInDirectory(_subtitleCacheDir),
            OpenSubtitlesFilesDeleted = DeleteFilesInDirectory(_openSubtitlesDir),
            SidecarFilesDeleted = DeleteLibrarySidecars()
        };

        _subtitleMemory.Clear();
        _prepareQueueStore.Clear();
        result.PrepareQueueCleared = true;

        _logger.LogInformation(
            "Cleared Look it up generated files: cache={Cache}, subtitles={Subs}, opensubtitles={Os}, sidecars={Sidecars}",
            result.CacheFilesDeleted,
            result.SubtitleCacheFilesDeleted,
            result.OpenSubtitlesFilesDeleted,
            result.SidecarFilesDeleted);

        return result;
    }

    private int DeleteLibrarySidecars()
    {
        var removed = 0;
        try
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                IsVirtualItem = false,
                MediaTypes = [MediaType.Video]
            });

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Path))
                {
                    continue;
                }

                try
                {
                    var dir = Path.GetDirectoryName(item.Path);
                    var stem = Path.GetFileNameWithoutExtension(item.Path);
                    if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem))
                    {
                        continue;
                    }

                    var sidecar = Path.Combine(dir, stem + ".lookitup.json");
                    if (!File.Exists(sidecar))
                    {
                        continue;
                    }

                    File.Delete(sidecar);
                    removed++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not delete Look it up sidecar for {Item}", item.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed scanning library for Look it up sidecars");
        }

        return removed;
    }

    private int DeleteFilesInDirectory(string directory)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete Look it up file {Path}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed clearing directory {Dir}", directory);
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextAnnotation>> GetAnnotationsAsync(
        Guid itemId,
        bool forceRescan,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
        {
            return Array.Empty<ContextAnnotation>();
        }

        if (!forceRescan && TryGetPrepared(itemId, out var cached) && cached is not null)
        {
            if (cached.Disabled)
            {
                return Array.Empty<ContextAnnotation>();
            }

            return FilterPlaybackAnnotations(cached.Annotations);
        }

        // Playback should usually hit precomputed data. On-demand prepare is opt-in.
        if (!forceRescan && !config.ScanOnPlayback)
        {
            _logger.LogDebug("No prepared annotations for {ItemId}; run library prepare", itemId);
            return Array.Empty<ContextAnnotation>();
        }

        var prepared = await PrepareItemAsync(itemId, force: true, cancellationToken)
            .ConfigureAwait(false);
        if (prepared.Cache?.Disabled == true)
        {
            return Array.Empty<ContextAnnotation>();
        }

        return FilterPlaybackAnnotations(prepared.Cache?.Annotations ?? []);
    }

    /// <inheritdoc />
    public async Task<PrepareAheadResult> PrepareAheadAsync(
        Guid itemId,
        long playbackMs,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.IncrementalPrepareOnPlayback)
        {
            return new PrepareAheadResult { Mode = "disabled" };
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return new PrepareAheadResult { Mode = "not-found", Warning = "Item not found." };
        }

        var gate = _aheadLocks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new PrepareAheadResult
            {
                Changed = false,
                Cache = _store.Get(itemId),
                Mode = "busy"
            };
        }

        try
        {
            var existing = _store.Get(itemId);
            var priorDisabled = existing?.Disabled ?? false;
            ItemAnnotationCache cache;
            if (existing is not null && existing.Version >= CurrentCacheVersion)
            {
                cache = existing;
            }
            else
            {
                cache = new ItemAnnotationCache
                {
                    ItemId = itemId,
                    Version = CurrentCacheVersion,
                    Disabled = priorDisabled,
                    Annotations = []
                };
            }

            if (cache.Disabled)
            {
                return new PrepareAheadResult { Changed = false, Cache = cache, Mode = "disabled" };
            }

            SubtitleContent? subtitle;
            try
            {
                subtitle = await ResolveSubtitleContentAsync(item, config.PreferredSubtitleLanguages, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OpenSubtitlesRateLimitedException ex)
            {
                return new PrepareAheadResult { Mode = "failed", Warning = ex.Message, Cache = cache };
            }

            if (subtitle is null)
            {
                cache.PrepareOutcome = "no-subtitles";
                cache.ScannedAtUtc = DateTime.UtcNow;
                PersistCache(item, cache, config);
                return new PrepareAheadResult
                {
                    Changed = true,
                    Cache = cache,
                    Mode = "none",
                    Warning = "No readable text subtitles found."
                };
            }

            var cues = _subtitleParser.Parse(subtitle.Content, subtitle.Label);
            if (cues.Count == 0)
            {
                return new PrepareAheadResult
                {
                    Changed = false,
                    Cache = cache,
                    Mode = "none",
                    Warning = "No subtitle cues parsed."
                };
            }

            var durationMs = cues.Max(c => c.EndMs);
            if (item.RunTimeTicks is > 0)
            {
                durationMs = Math.Max(durationMs, item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond);
            }

            if (cache.FullyPrepared || cache.PreparedThroughMs >= durationMs)
            {
                cache.FullyPrepared = true;
                cache.PreparedThroughMs = durationMs;
                return new PrepareAheadResult
                {
                    Changed = false,
                    Cache = cache,
                    SubtitleDurationMs = durationMs,
                    Mode = "cache"
                };
            }

            var windowMs = Math.Clamp(config.IncrementalPrepareWindowMs, 60_000, 900_000);
            playbackMs = Math.Max(0, playbackMs);
            var fromMs = cache.PreparedThroughMs;
            var toMs = Math.Min(Math.Max(fromMs + windowMs, playbackMs + windowMs), durationMs);
            if (fromMs >= toMs)
            {
                return new PrepareAheadResult
                {
                    Changed = false,
                    Cache = cache,
                    SubtitleDurationMs = durationMs,
                    Mode = "skipped"
                };
            }

            var knownBefore = new HashSet<string>(
                cache.Annotations.Select(a => a.Term),
                StringComparer.OrdinalIgnoreCase);
            var minLen = Math.Max(2, config.MinEntityLength);
            var excludedCast = BuildCastExcludeNames(item, minLen);
            var mediaContext = BuildAiMediaContext(item, excludedCast);

            cache.Version = CurrentCacheVersion;
            cache.SubtitlePath ??= subtitle.Label;
            cache.SubtitleSource ??= subtitle.Source;
            cache.MatchedBy ??= subtitle.MatchedBy;
            cache.MovieHash ??= subtitle.MovieHash;
            cache.DurationCheckOk = PassesDurationCheck(item, cues);

            var (window, mode, warning) = await _incrementalEngine
                .PrepareWindowAsync(
                    cache,
                    cues,
                    item.Name ?? "Unknown",
                    excludedCast,
                    mediaContext,
                    fromMs,
                    toMs,
                    config,
                    cancellationToken)
                .ConfigureAwait(false);

            cache.FullyPrepared = cache.PreparedThroughMs >= durationMs;
            cache.PrepareOutcome = cache.Annotations.Count > 0
                ? "success"
                : string.Equals(cache.PrepareOutcome, "no-subtitles", StringComparison.OrdinalIgnoreCase)
                    ? "no-subtitles"
                    : "success";

            if (item is Episode ep)
            {
                cache.SeriesName ??= ep.SeriesName;
                cache.SeasonNumber ??= ep.ParentIndexNumber;
                cache.EpisodeNumber ??= ep.IndexNumber;
            }

            PersistCache(item, cache, config);

            var added = cache.Annotations
                .Where(a => !knownBefore.Contains(a.Term))
                .ToList();

            return new PrepareAheadResult
            {
                Changed = window.AnnotationsAdded > 0 || cache.PreparedThroughMs > fromMs,
                Added = added,
                Cache = cache,
                Window = window,
                SubtitleDurationMs = durationMs,
                Mode = mode,
                Warning = warning
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Look it up prepare-ahead failed for {ItemId}", itemId);
            return new PrepareAheadResult { Mode = "failed", Warning = ex.Message };
        }
        finally
        {
            gate.Release();
        }
    }

    private static IReadOnlyList<ContextAnnotation> FilterPlaybackAnnotations(
        IReadOnlyList<ContextAnnotation> annotations)
    {
        if (annotations.Count == 0)
        {
            return annotations;
        }

        return annotations
            .Where(a => !OpenAiCompatibleEntityExtractor.IsSongOrMusicWork(a.Term, a.Kind, a.Summary))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<NameCandidatesResult> GetNameCandidatesAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return new NameCandidatesResult
            {
                ItemId = itemId,
                Warning = "Item not found."
            };
        }

        var config = Plugin.Instance?.Configuration;
        var preferred = config?.PreferredSubtitleLanguages ?? "en";
        var subtitle = await ResolveSubtitleContentAsync(item, preferred, cancellationToken)
            .ConfigureAwait(false);
        if (subtitle is null)
        {
            return new NameCandidatesResult
            {
                ItemId = itemId,
                ItemName = item.Name,
                Warning = "No readable text subtitles found."
            };
        }

        var cues = _subtitleParser.Parse(subtitle.Content, subtitle.Label);
        var max = Math.Max(1, config?.MaxAnnotationsPerItem ?? 40);
        var minLen = Math.Max(2, config?.MinEntityLength ?? 3);
        var excludedCast = BuildCastExcludeNames(item, minLen);
        var candidates = _nameCandidateFinder.Find(cues, item.Name, excludedCast, minLen, max);

        _logger.LogInformation(
            "Look it up name candidates for {Item}: {Count} from {Subtitle} ({Cues} cues), excluded cast tokens={Excluded}",
            item.Name,
            candidates.Count,
            subtitle.Label,
            cues.Count,
            excludedCast.Count);

        return new NameCandidatesResult
        {
            ItemId = itemId,
            ItemName = item.Name,
            Subtitle = subtitle.Label,
            CueCount = cues.Count,
            Candidates = candidates,
            ExcludedCastNames = excludedCast.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <inheritdoc />
    public async Task<PreparePreviewResult> GetPreparePreviewAsync(
        Guid rootItemId,
        int? suggestedNamesPerItem,
        CancellationToken cancellationToken)
    {
        var root = _libraryManager.GetItemById(rootItemId);
        if (root is null)
        {
            return new PreparePreviewResult
            {
                RootItemId = rootItemId,
                Warning = "Item not found."
            };
        }

        var config = Plugin.Instance?.Configuration;
        var defaultN = Math.Clamp(config?.AiNamesPerPrepare ?? 5, 1, 200);
        var suggestedN = Math.Clamp(suggestedNamesPerItem ?? defaultN, 1, 200);
        List<BaseItem> targets;
        try
        {
            targets = ResolvePrepareTargets(root);
        }
        catch (Exception ex)
        {
            return new PreparePreviewResult
            {
                RootItemId = rootItemId,
                RootItemName = root.Name,
                RootItemType = root switch
                {
                    Series => "Series",
                    Season => "Season",
                    Episode => "Episode",
                    Movie => "Movie",
                    _ => root.GetType().Name
                },
                DefaultNamesPerPrepare = defaultN,
                SuggestedNamesPerItem = suggestedN,
                Warning = ex.Message
            };
        }

        var items = new List<PreparePreviewItem>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = await BuildPreviewItemAsync(target, suggestedN, cancellationToken)
                .ConfigureAwait(false);
            items.Add(preview);
        }

        return new PreparePreviewResult
        {
            RootItemId = rootItemId,
            RootItemName = root.Name,
            RootItemType = root switch
            {
                Series => "Series",
                Season => "Season",
                Episode => "Episode",
                Movie => "Movie",
                _ => root.GetType().Name
            },
            DefaultNamesPerPrepare = defaultN,
            SuggestedNamesPerItem = suggestedN,
            Items = items,
            Warning = items.Count == 0 ? "No episodes/movies found under this item." : null
        };
    }

    private async Task<PreparePreviewItem> BuildPreviewItemAsync(
        BaseItem item,
        int suggestedN,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var already = TryGetPrepared(item.Id, out _);
        var preferred = config?.PreferredSubtitleLanguages ?? "en";
        var subtitle = await ResolveSubtitleContentAsync(item, preferred, cancellationToken)
            .ConfigureAwait(false);
        if (subtitle is null)
        {
            return new PreparePreviewItem
            {
                ItemId = item.Id,
                Name = item.Name,
                SeasonNumber = item.ParentIndexNumber,
                EpisodeNumber = item.IndexNumber,
                AlreadyPrepared = already,
                Warning = "No readable text subtitles found."
            };
        }

        var cues = _subtitleParser.Parse(subtitle.Content, subtitle.Label);
        var minLen = Math.Max(2, config?.MinEntityLength ?? 3);
        var excludedCast = BuildCastExcludeNames(item, minLen);
        // Load every local candidate; suggestedN only controls which boxes start checked.
        const int previewCandidateCap = 500;
        var ranked = _nameCandidateFinder
            .Find(cues, item.Name, excludedCast, minLen, previewCandidateCap)
            .ToList();
        var suggested = new HashSet<string>(
            SelectAiBatch(ranked, suggestedN).Select(c => c.Term),
            StringComparer.OrdinalIgnoreCase);

        return new PreparePreviewItem
        {
            ItemId = item.Id,
            Name = item.Name,
            SeasonNumber = item.ParentIndexNumber,
            EpisodeNumber = item.IndexNumber,
            Subtitle = subtitle.Label,
            CueCount = cues.Count,
            AlreadyPrepared = already,
            Candidates = ranked.Select(c => new PreparePreviewCandidate
            {
                Term = c.Term,
                Score = c.Score,
                Reason = c.Reason,
                StartMs = c.StartMs,
                EndMs = c.EndMs,
                CueText = c.CueText,
                Suggested = suggested.Contains(c.Term)
            }).ToList()
        };
    }

    private List<BaseItem> ResolvePrepareTargets(BaseItem root)
    {
        if (root is Movie or Episode)
        {
            return string.IsNullOrWhiteSpace(root.Path) ? [] : [root];
        }

        if (root is not Series and not Season)
        {
            throw new InvalidOperationException(
                "Look it up can prepare a Series, Season, Episode, or Movie.");
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [root.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
                MediaTypes = [MediaType.Video]
            })
            .Where(i => !string.IsNullOrWhiteSpace(i.Path))
            .OrderBy(i => i.ParentIndexNumber ?? 0)
            .ThenBy(i => i.IndexNumber ?? 0)
            .ThenBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<NameCandidate> FilterCandidatesBySelectedTerms(
        IReadOnlyList<NameCandidate> ranked,
        IReadOnlyList<string> selectedTerms,
        IReadOnlyList<SubtitleCue> cues)
    {
        var wanted = selectedTerms
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byTerm = ranked.ToDictionary(c => c.Term, StringComparer.OrdinalIgnoreCase);
        var result = new List<NameCandidate>();
        foreach (var term in wanted)
        {
            if (byTerm.TryGetValue(term, out var existing))
            {
                result.Add(existing);
                continue;
            }

            var anchored = FindEarliestMention(cues, term);
            if (anchored is null)
            {
                continue;
            }

            result.Add(new NameCandidate
            {
                Term = term,
                StartMs = anchored.Value.StartMs,
                EndMs = anchored.Value.EndMs,
                CueText = string.Empty,
                Score = 50,
                Reason = "user-selected"
            });
        }

        return result;
    }

    private HashSet<string> BuildCastExcludeNames(BaseItem item, int minLength)
    {
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPeopleToExclude(exclude, _libraryManager.GetPeople(item), minLength);

        if (item is Episode episode)
        {
            try
            {
                var series = episode.Series ?? (episode.SeriesId != Guid.Empty
                    ? _libraryManager.GetItemById(episode.SeriesId)
                    : null);
                if (series is not null)
                {
                    AddPeopleToExclude(exclude, _libraryManager.GetPeople(series), minLength);
                    AddNameTokens(exclude, series.Name, minLength);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load series cast for {Item}", item.Name);
            }
        }
        else if (item is Season season)
        {
            AddNameTokens(exclude, season.SeriesName, minLength);
        }

        return exclude;
    }

    private AiMediaContext BuildAiMediaContext(BaseItem item, IReadOnlyCollection<string> excludedCast)
    {
        string showName;
        string? episodeName = null;

        if (item is Episode episode)
        {
            episodeName = episode.Name;
            showName = episode.SeriesName;
            if (string.IsNullOrWhiteSpace(showName))
            {
                try
                {
                    var series = episode.Series ?? (episode.SeriesId != Guid.Empty
                        ? _libraryManager.GetItemById(episode.SeriesId)
                        : null);
                    showName = series?.Name ?? episode.Name ?? "Unknown show";
                }
                catch
                {
                    showName = episode.Name ?? "Unknown show";
                }
            }
        }
        else if (item is Season season)
        {
            showName = season.SeriesName;
            if (string.IsNullOrWhiteSpace(showName))
            {
                try
                {
                    var series = season.SeriesId != Guid.Empty
                        ? _libraryManager.GetItemById(season.SeriesId)
                        : null;
                    showName = series?.Name ?? season.Name ?? "Unknown show";
                }
                catch
                {
                    showName = season.Name ?? "Unknown show";
                }
            }

            episodeName = season.Name;
        }
        else
        {
            // Movies and other items: the item name is the show/title.
            showName = item.Name ?? "Unknown title";
        }

        // Prefer fuller names for the AI hint (role + person), still capped.
        var castHint = excludedCast
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length >= 2)
            .OrderByDescending(n => n.Contains(' ', StringComparison.Ordinal))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

        return new AiMediaContext
        {
            ShowName = showName,
            EpisodeName = episodeName,
            KnownCastNames = castHint
        };
    }

    /// <summary>
    /// Picks AI verify targets, preferring earlier shorter Cap+Cap forms over later long phrases
    /// (e.g. "Jon Voight" @ 0:59 over "Jon Voight's LeBaron" @ 2:31),
    /// while still queuing the possessive tail (LeBaron) separately.
    /// </summary>
    private static List<NameCandidate> SelectAiBatch(IReadOnlyList<NameCandidate> ranked, int limit)
    {
        var batch = new List<NameCandidate>(limit);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in ranked)
        {
            if (batch.Count >= limit)
            {
                break;
            }

            var preferred = PreferEarlierShorterForm(candidate, ranked);
            if (used.Add(preferred.Term))
            {
                batch.Add(preferred);
            }

            // Collapsing "Jon Voight's LeBaron" → "Jon Voight" must not drop "LeBaron".
            if (batch.Count >= limit)
            {
                break;
            }

            var tail = GetPossessiveTail(candidate.Term);
            if (tail is null || !used.Add(tail))
            {
                continue;
            }

            NameCandidate? tailCandidate = null;
            foreach (var other in ranked)
            {
                if (!other.Term.Equals(tail, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (tailCandidate is null || other.StartMs < tailCandidate.StartMs)
                {
                    tailCandidate = other;
                }
            }

            batch.Add(tailCandidate ?? new NameCandidate
            {
                Term = tail,
                StartMs = candidate.StartMs,
                EndMs = candidate.EndMs,
                CueText = candidate.CueText,
                Score = candidate.Score,
                Reason = "possessive-tail"
            });
        }

        return batch.OrderBy(c => c.StartMs).ToList();
    }

    private static NameCandidate PreferEarlierShorterForm(
        NameCandidate candidate,
        IReadOnlyList<NameCandidate> pool)
    {
        var parts = candidate.Term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return candidate;
        }

        var head2 = parts[0] + " " + StripPossessive(parts[1]);
        NameCandidate? best = null;
        foreach (var other in pool)
        {
            if (!other.Term.Equals(head2, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (other.StartMs <= candidate.StartMs && (best is null || other.StartMs < best.StartMs))
            {
                best = other;
            }
        }

        return best ?? candidate;
    }

    private static string? GetPossessiveTail(string term)
    {
        var parts = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!IsPossessiveToken(parts[i]))
            {
                continue;
            }

            var tail = string.Join(' ', parts.Skip(i + 1));
            return string.IsNullOrWhiteSpace(tail) ? null : tail;
        }

        return null;
    }

    private static bool IsPossessiveToken(string token)
    {
        return token.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
               || token.EndsWith("’s", StringComparison.OrdinalIgnoreCase)
               || token.EndsWith("'", StringComparison.Ordinal)
               || token.EndsWith("’", StringComparison.Ordinal);
    }

    private static string StripPossessive(string token)
    {
        if (token.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
            || token.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
        {
            return token[..^2];
        }

        if (token.EndsWith("'", StringComparison.Ordinal) || token.EndsWith("’", StringComparison.Ordinal))
        {
            return token[..^1];
        }

        return token;
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

    /// <summary>
    /// Finds the earliest subtitle cue that mentions <paramref name="term"/> (case-insensitive, word-aware).
    /// So when AI canonicalizes "Jon Voight's LeBaron" → "Jon Voight", the popup fires on the first mention.
    /// </summary>
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
            // Allow possessive / punctuation after the name: Jon Voight's, Jon Voight,
            if (beforeOk && (afterOk || text[afterIndex] is '\'' or '’' or ',' or '.' or '!' or '?' or ';' or ':'))
            {
                return true;
            }

            idx = text.IndexOf(term, idx + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void AddPeopleToExclude(
        HashSet<string> exclude,
        IReadOnlyList<PersonInfo> people,
        int minLength)
    {
        foreach (var person in people)
        {
            AddNameTokens(exclude, person.Role, minLength);
            AddNameTokens(exclude, person.Name, minLength);
        }
    }

    private static void AddNameTokens(HashSet<string> exclude, string? value, int minLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var cleaned = value.Trim();
        if (cleaned.Length >= minLength)
        {
            exclude.Add(cleaned);
        }

        foreach (var part in cleaned.Split(
                     [' ', '/', ',', '-', '—', '–', '(', ')'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length >= minLength)
            {
                exclude.Add(part);
            }
        }
    }

    /// <inheritdoc />
    public async Task<PrepareItemResult> PrepareItemAsync(
        Guid itemId,
        bool force,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? selectedTerms = null)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled)
            {
                return new PrepareItemResult { Warning = "Plugin disabled." };
            }

            var priorDisabled = false;
            if (_store.Get(itemId) is { } prior)
            {
                priorDisabled = prior.Disabled;
            }

            if (!force && TryGetPrepared(itemId, out var existing) && existing is not null
                && IsSuccessfullyPrepared(existing))
            {
                return new PrepareItemResult { Cache = existing, Mode = "cache" };
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                _logger.LogWarning("Look it up prepare: item {ItemId} not found", itemId);
                return new PrepareItemResult { Warning = "Item not found." };
            }

            SubtitleContent? subtitle;
            try
            {
                subtitle = await ResolveSubtitleContentAsync(item, config.PreferredSubtitleLanguages, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OpenSubtitlesRateLimitedException ex)
            {
                return new PrepareItemResult { Warning = ex.Message };
            }

            if (subtitle is null)
            {
                _logger.LogInformation("No readable text subtitles found for {Item}", item.Name);
                var empty = SaveCache(
                    item,
                    annotations: [],
                    subtitlePath: null,
                    subtitleSource: null,
                    matchedBy: null,
                    movieHash: null,
                    durationOk: true,
                    outcome: "no-subtitles",
                    disabled: priorDisabled);
                MaybeWriteSidecar(item, empty, config.WriteSidecarFiles);
                return new PrepareItemResult
                {
                    Cache = empty,
                    Mode = "none",
                    Warning = "No readable text subtitles found."
                };
            }

            var cues = _subtitleParser.Parse(subtitle.Content, subtitle.Label);
            var durationOk = PassesDurationCheck(item, cues);
            if (!durationOk)
            {
                _logger.LogWarning(
                    "Subtitle duration check failed for {Item} ({Label}); continuing with caution",
                    item.Name,
                    subtitle.Label);
            }

            var max = Math.Max(1, config.MaxAnnotationsPerItem);
            List<ContextAnnotation> annotations;
            string mode;
            string? warning = null;
            string? aiBaseUrl = null;
            string? aiModel = null;
            string outcome = "success";

            if (_aiExtractor.IsConfigured(config))
            {
                mode = "ai";
                aiModel = OpenAiCompatibleEntityExtractor.ResolveModel(config);
                aiBaseUrl = OpenAiCompatibleEntityExtractor.ResolveBaseUrl(config, aiModel);

                var minLen = Math.Max(2, config.MinEntityLength);
                var excludedCast = BuildCastExcludeNames(item, minLen);
                // 0 = unlimited (safety-capped later in AI extractor).
                var nameLimit = config.AiNamesPerPrepare <= 0
                    ? 250
                    : Math.Clamp(config.AiNamesPerPrepare, 1, 250);
                const int prepareCandidateCap = 750;
                var rankedLimit = selectedTerms is { Count: > 0 }
                    ? Math.Max(prepareCandidateCap, selectedTerms.Count)
                    : prepareCandidateCap;
                var ranked = _nameCandidateFinder
                    .Find(cues, item.Name, excludedCast, minLen, rankedLimit)
                    .ToList();

                List<NameCandidate> nameCandidates;
                if (selectedTerms is { Count: > 0 })
                {
                    nameCandidates = FilterCandidatesBySelectedTerms(ranked, selectedTerms, cues);
                    if (nameCandidates.Count == 0)
                    {
                        return new PrepareItemResult
                        {
                            Mode = "ai",
                            AiBaseUrl = aiBaseUrl,
                            AiModel = aiModel,
                            Warning = "None of the selected terms were found in subtitles."
                        };
                    }
                }
                else
                {
                    // Send all ranked candidates (capped by nameLimit / AI safety).
                    nameCandidates = ranked.Take(nameLimit).ToList();
                }

                if (nameCandidates.Count == 0)
                {
                    var noCand = SaveCache(
                        item,
                        annotations: [],
                        subtitlePath: subtitle.Label,
                        subtitleSource: subtitle.Source,
                        matchedBy: subtitle.MatchedBy,
                        movieHash: subtitle.MovieHash,
                        durationOk: durationOk,
                        outcome: "no-candidates",
                        disabled: priorDisabled);
                    MaybeWriteSidecar(item, noCand, config.WriteSidecarFiles);
                    return new PrepareItemResult
                    {
                        Cache = noCand,
                        Mode = "ai",
                        AiBaseUrl = aiBaseUrl,
                        AiModel = aiModel,
                        Warning = "No local name candidates to verify."
                    };
                }

                _logger.LogInformation(
                    "Look it up preparing {Item} with AI ({Provider}/{Model}) via {BaseUrl}: verifying {Count} names",
                    item.Name,
                    config.AiProvider,
                    aiModel,
                    aiBaseUrl,
                    nameCandidates.Count);

                var mediaContext = BuildAiMediaContext(item, excludedCast);
                _logger.LogInformation(
                    "Look it up AI media context: show={Show} episode={Episode} castHints={CastCount}",
                    mediaContext.ShowName,
                    mediaContext.EpisodeName ?? "-",
                    mediaContext.KnownCastNames.Count);

                var aiResult = await _aiExtractor
                    .ResolveNamesAsync(
                        mediaContext,
                        nameCandidates,
                        config,
                        cancellationToken)
                    .ConfigureAwait(false);

                warning = aiResult.Warning;
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    _logger.LogWarning(
                        "Look it up AI prepare warning for {Item}: {Warning}",
                        item.Name,
                        warning);
                }

                var popupMs = Math.Max(config.PopupDurationMs, 8000);
                var wikiLang = string.IsNullOrWhiteSpace(config.WikipediaLanguage) ? "en" : config.WikipediaLanguage;
                var built = new List<ContextAnnotation>();
                foreach (var m in aiResult.Mentions)
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
                    if (anchored is { } hit && hit.StartMs != m.StartMs)
                    {
                        _logger.LogInformation(
                            "Look it up re-anchored {Term} from {OldMs}ms → earliest cue {NewMs}ms",
                            term,
                            m.StartMs,
                            hit.StartMs);
                    }

                    string? pageUrl = null;
                    string? imageUrl = null;
                    if (string.Equals(kind, "person", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var wiki = await _wikipedia
                                .LookupAsync(term, wikiLang, cancellationToken)
                                .ConfigureAwait(false);
                            if (wiki.Found)
                            {
                                pageUrl = wiki.Url;
                                imageUrl = wiki.ImageUrl;
                                _logger.LogInformation(
                                    "Look it up Wikipedia image for {Term}: {HasImage}",
                                    term,
                                    !string.IsNullOrWhiteSpace(imageUrl));
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

                annotations = built
                    .GroupBy(a => a.Term, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderBy(a => a.StartMs).First())
                    .OrderBy(a => a.StartMs)
                    .Take(max)
                    .ToList();
            }
            else
            {
                mode = "legacy";
                _logger.LogInformation(
                    "Look it up preparing {Item} with legacy Wikipedia heuristics (set AiProvider + AiApiKey for AI)",
                    item.Name);
                annotations = await PrepareWithHeuristicsAsync(cues, config, max, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (annotations.Count == 0 && string.IsNullOrWhiteSpace(warning))
            {
                outcome = "no-candidates";
            }
            else if (annotations.Count == 0 && !string.IsNullOrWhiteSpace(warning))
            {
                outcome = "failed";
            }

            var cache = SaveCache(
                item,
                annotations,
                subtitle.Label,
                subtitle.Source,
                subtitle.MatchedBy,
                subtitle.MovieHash,
                durationOk,
                outcome,
                priorDisabled);
            cache.PreparedThroughMs = cues.Max(c => c.EndMs);
            cache.FullyPrepared = true;
            _store.Save(cache);
            MaybeWriteSidecar(item, cache, config.WriteSidecarFiles);

            _logger.LogInformation(
                "Look it up prepared {Item}: {Count} annotations from {Subtitle} matchedBy={MatchedBy} durationOk={DurationOk}",
                item.Name,
                annotations.Count,
                subtitle.Label,
                subtitle.MatchedBy,
                durationOk);

            return new PrepareItemResult
            {
                Cache = cache,
                Mode = mode,
                AiBaseUrl = aiBaseUrl,
                AiModel = aiModel,
                Warning = warning
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Look it up prepare failed for item {ItemId}: {Message}", itemId, ex.Message);
            return new PrepareItemResult { Warning = ex.Message };
        }
    }

    private async Task<List<ContextAnnotation>> PrepareWithHeuristicsAsync(
        IReadOnlyList<SubtitleCue> cues,
        Configuration.PluginConfiguration config,
        int max,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(ContextAnnotation Annotation, int Score)>();
        var usedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cue in cues)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

                EntityLookupResult lookup;
                try
                {
                    lookup = await _wikipedia
                        .LookupAsync(entity, config.WikipediaLanguage, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Wikipedia lookup failed for {Entity}", entity);
                    continue;
                }

                if (!lookup.Found)
                {
                    continue;
                }

                var score = ScoreEntity(entity, lookup.Title);
                var matchWindowMs = Math.Max(config.PopupDurationMs, score >= 30 ? 6000 : 4000);
                candidates.Add((new ContextAnnotation
                {
                    Term = lookup.Title,
                    Summary = $"{lookup.Title}: {lookup.Summary}",
                    Url = lookup.Url,
                    ImageUrl = lookup.ImageUrl,
                    Kind = "other",
                    StartMs = cue.StartMs,
                    EndMs = Math.Max(cue.EndMs, cue.StartMs + matchWindowMs)
                }, score));
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Annotation.StartMs)
            .Take(max)
            .Select(c => c.Annotation)
            .OrderBy(a => a.StartMs)
            .ToList();
    }

    private ItemAnnotationCache SaveCache(
        BaseItem item,
        List<ContextAnnotation> annotations,
        string? subtitlePath,
        string? subtitleSource,
        string? matchedBy,
        string? movieHash,
        bool durationOk,
        string outcome,
        bool disabled)
    {
        string? seriesName = null;
        int? season = null;
        int? episode = null;
        if (item is Episode ep)
        {
            seriesName = ep.SeriesName;
            season = ep.ParentIndexNumber;
            episode = ep.IndexNumber;
        }

        var cache = new ItemAnnotationCache
        {
            ItemId = item.Id,
            Version = CurrentCacheVersion,
            ScannedAtUtc = DateTime.UtcNow,
            SubtitlePath = subtitlePath,
            SubtitleSource = subtitleSource,
            SubtitleHash = annotations.Count > 0 || subtitlePath is not null
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subtitlePath ?? string.Empty))).ToLowerInvariant()
                : null,
            MovieHash = movieHash,
            MatchedBy = matchedBy,
            DurationCheckOk = durationOk,
            PrepareOutcome = outcome,
            SeriesName = seriesName,
            SeasonNumber = season,
            EpisodeNumber = episode,
            Disabled = disabled,
            Annotations = annotations
        };
        _store.Save(cache);
        return cache;
    }

    private void PersistCache(
        BaseItem item,
        ItemAnnotationCache cache,
        Configuration.PluginConfiguration config)
    {
        cache.ScannedAtUtc = DateTime.UtcNow;
        _store.Save(cache);
        MaybeWriteSidecar(item, cache, config.WriteSidecarFiles);
    }

    private static bool PassesDurationCheck(BaseItem item, IReadOnlyList<SubtitleCue> cues)
    {
        if (cues.Count < 8 || item.RunTimeTicks is null or <= 0)
        {
            return true;
        }

        var runtimeMs = item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond;
        if (runtimeMs < 60_000)
        {
            return true;
        }

        var lastEnd = cues.Max(c => c.EndMs);
        if (lastEnd > runtimeMs * 1.12)
        {
            return false;
        }

        if (cues.Count >= 40 && lastEnd < runtimeMs * 0.70)
        {
            return false;
        }

        return true;
    }

    private void MaybeWriteSidecar(BaseItem item, ItemAnnotationCache cache, bool enabled)
    {
        if (!enabled || string.IsNullOrWhiteSpace(item.Path))
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(item.Path);
            var stem = Path.GetFileNameWithoutExtension(item.Path);
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem))
            {
                return;
            }

            var sidecar = Path.Combine(dir, stem + ".lookitup.json");
            var json = System.Text.Json.JsonSerializer.Serialize(
                cache,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(sidecar, json);
            _logger.LogDebug("Wrote Look it up sidecar {Path}", sidecar);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write Look it up sidecar for {Item}", item.Name);
        }
    }

    private static int ScoreEntity(string query, string title)
    {
        var score = 0;
        var qWords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tWords = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Prefer "Jon Voight", "Midnight Cowboy" over single tokens.
        if (qWords.Length >= 2)
        {
            score += 40;
        }
        else
        {
            score += 5;
        }

        if (tWords.Length >= 2)
        {
            score += 15;
        }

        // Person-like First Last
        if (qWords.Length == 2
            && qWords.All(w => w.Length >= 2 && char.IsUpper(w[0])))
        {
            score += 20;
        }

        if (title.Contains('(', StringComparison.Ordinal))
        {
            score -= 25;
        }

        if (title.StartsWith("List of", StringComparison.OrdinalIgnoreCase))
        {
            score -= 50;
        }

        return score;
    }

    private async Task<SubtitleContent?> ResolveSubtitleContentAsync(
        BaseItem item,
        string preferredLanguages,
        CancellationToken cancellationToken)
    {
        var preferred = (preferredLanguages ?? "en")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeLang)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1) External sidecar / indexed external files (strict episode matching)
        var external = FindExternalSubtitle(item, preferred);
        if (external is not null)
        {
            try
            {
                var content = await File.ReadAllTextAsync(external.Value.Path, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogInformation(
                        "Using external subtitle for {Item}: {Path} matchedBy={MatchedBy} score={Score}",
                        item.Name,
                        external.Value.Path,
                        external.Value.MatchedBy,
                        external.Value.Score);
                    return new SubtitleContent(content, external.Value.Path, "external", external.Value.MatchedBy);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read subtitle file {Path}", external.Value.Path);
            }
        }

        // 2) OpenSubtitles (moviehash first) when enabled
        var config = Plugin.Instance?.Configuration;
        if (config is not null && _openSubtitles.IsConfigured(config))
        {
            var dest = Path.Combine(_openSubtitlesDir, $"{item.Id:N}.srt");
            var os = await _openSubtitles
                .TryDownloadAsync(item, config, dest, cancellationToken)
                .ConfigureAwait(false);
            if (os is not null)
            {
                _logger.LogInformation(
                    "Using OpenSubtitles for {Item}: matchedBy={MatchedBy}",
                    item.Name,
                    os.MatchedBy);
                return new SubtitleContent(os.Content, os.Path, "opensubtitles", os.MatchedBy, os.MovieHash);
            }
        }

        // 3) Cached embedded extract (skip ffmpeg on repeat preview/prepare)
        if (TryReadCachedEmbeddedSubtitle(item, out var cached))
        {
            _logger.LogInformation("Using cached embedded subtitles for {Item}", item.Name);
            return cached;
        }

        // 4) Embedded text tracks via ffmpeg
        var extracted = await ExtractEmbeddedSubtitleAsync(item, preferred, cancellationToken)
            .ConfigureAwait(false);
        if (extracted is not null)
        {
            TryWriteCachedEmbeddedSubtitle(item, extracted);
        }

        return extracted;
    }

    private bool TryReadCachedEmbeddedSubtitle(BaseItem item, out SubtitleContent? content)
    {
        content = null;
        if (_subtitleMemory.TryGetValue(item.Id, out var mem) && !string.IsNullOrWhiteSpace(mem.Content))
        {
            content = mem;
            return true;
        }

        var srtPath = GetSubtitleCacheSrtPath(item.Id);
        var labelPath = GetSubtitleCacheLabelPath(item.Id);
        if (!File.Exists(srtPath) || !File.Exists(labelPath))
        {
            return false;
        }

        try
        {
            // Invalidate when the media file is newer than the cache.
            if (!string.IsNullOrWhiteSpace(item.Path) && File.Exists(item.Path))
            {
                var mediaWrite = File.GetLastWriteTimeUtc(item.Path);
                var cacheWrite = File.GetLastWriteTimeUtc(srtPath);
                if (mediaWrite > cacheWrite)
                {
                    return false;
                }
            }

            var text = File.ReadAllText(srtPath);
            var label = File.ReadAllText(labelPath).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            content = new SubtitleContent(
                text,
                string.IsNullOrWhiteSpace(label) ? "cached-embedded" : label,
                "embedded",
                "embedded");
            _subtitleMemory[item.Id] = content;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed reading subtitle cache for {Item}", item.Name);
            return false;
        }
    }

    private void TryWriteCachedEmbeddedSubtitle(BaseItem item, SubtitleContent extracted)
    {
        try
        {
            Directory.CreateDirectory(_subtitleCacheDir);
            File.WriteAllText(GetSubtitleCacheSrtPath(item.Id), extracted.Content);
            File.WriteAllText(GetSubtitleCacheLabelPath(item.Id), extracted.Label ?? "embedded");
            _subtitleMemory[item.Id] = extracted;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed writing subtitle cache for {Item}", item.Name);
        }
    }

    private string GetSubtitleCacheSrtPath(Guid itemId)
        => Path.Combine(_subtitleCacheDir, itemId.ToString("N") + ".srt");

    private string GetSubtitleCacheLabelPath(Guid itemId)
        => Path.Combine(_subtitleCacheDir, itemId.ToString("N") + ".label");

    private async Task<SubtitleContent?> ExtractEmbeddedSubtitleAsync(
        BaseItem item,
        HashSet<string> preferred,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<MediaBrowser.Model.Dto.MediaSourceInfo> sources;
            try
            {
                sources = _mediaSourceManager.GetStaticMediaSources(item, enablePathSubstitution: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetStaticMediaSources failed for {Item}", item.Name);
                return null;
            }

            var mediaSource = sources.FirstOrDefault();
            if (mediaSource is null)
            {
                return null;
            }

            var candidates = mediaSource.MediaStreams
                .Where(s => s.Type == MediaStreamType.Subtitle && IsTextSubtitle(s))
                .Select(s => (Stream: s, Score: ScoreSubtitleStream(s, preferred)))
                .OrderByDescending(x => x.Score)
                .ToList();

            if (candidates.Count == 0)
            {
                _logger.LogInformation("No embedded text subtitle streams for {Item}", item.Name);
                return null;
            }

            foreach (var (stream, score) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _logger.LogInformation(
                        "Extracting embedded subtitle #{Index} ({Codec}/{Lang}, score {Score}) for {Item}",
                        stream.Index,
                        stream.Codec,
                        stream.Language,
                        score,
                        item.Name);

                    // Prefer our own single-stream ffmpeg. Jellyfin's ISubtitleEncoder.GetSubtitles
                    // demuxes ALL extractable tracks and can block for many minutes (ignores cancel on 10.11).
                    if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
                    {
                        _logger.LogWarning("No media path for embedded extract on {Item}", item.Name);
                        continue;
                    }

                    var content = await ExtractSubtitleStreamWithFfmpegAsync(item.Path, stream.Index, cancellationToken)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    var label = $"embedded:{stream.Index}:{stream.Codec}:{stream.Language ?? "und"}";
                    return new SubtitleContent(content, label, "embedded", "embedded");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to extract embedded subtitle #{Index} for {Item}",
                        stream.Index,
                        item.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedded subtitle extraction failed for {Item}", item.Name);
        }

        return null;
    }

    private async Task<string?> ExtractSubtitleStreamWithFfmpegAsync(
        string mediaPath,
        int streamIndex,
        CancellationToken cancellationToken)
    {
        var ffmpeg = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            _logger.LogWarning("ffmpeg path is not configured; cannot extract embedded subtitles quickly");
            return null;
        }

        var outPath = Path.Combine(Path.GetTempPath(), "lookitup-" + Guid.NewGuid().ToString("N") + ".srt");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Single stream only — avoids Jellyfin's ExtractAllExtractableSubtitles hang.
            foreach (var arg in new[]
                     {
                         "-hide_banner", "-nostdin", "-y",
                         "-i", mediaPath,
                         "-map", "0:" + streamIndex,
                         "-an", "-vn",
                         "-c:s", "srt",
                         outPath
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return null;
            }

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await using var killReg = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignored
                }
            });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignored
                }

                throw;
            }

            string stderr;
            try
            {
                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch
            {
                stderr = string.Empty;
            }

            try
            {
                await stdoutTask.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "ffmpeg subtitle extract failed (exit {Code}) for stream #{Index}: {Err}",
                    process.ExitCode,
                    streamIndex,
                    Truncate(stderr, 400));
                return null;
            }

            if (!File.Exists(outPath))
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(outPath, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            try
            {
                if (File.Exists(outPath))
                {
                    File.Delete(outPath);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value ?? string.Empty;
        }

        return value[..max] + "…";
    }

    private (string Path, string MatchedBy, int Score)? FindExternalSubtitle(BaseItem item, HashSet<string> preferred)
    {
        var candidates = new List<(string Path, string MatchedBy, int Score)>();
        int? season = item is Episode ep ? ep.ParentIndexNumber : null;
        int? episode = item is Episode ep2 ? ep2.IndexNumber : null;
        var mediaBase = !string.IsNullOrWhiteSpace(item.Path)
            ? Path.GetFileNameWithoutExtension(item.Path)
            : null;

        void Consider(string path, string matchedBy, int score)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            var fileName = Path.GetFileNameWithoutExtension(path);
            var episodeBonus = ScoreEpisodeAlignment(fileName, season, episode);
            // For TV: reject season-generic files when they don't encode SxxExx at all
            // and we have a better stem match available later — still allow with low score.
            if (season is not null && episode is not null && episodeBonus < 0)
            {
                score += episodeBonus;
            }
            else
            {
                score += episodeBonus;
            }

            candidates.Add((path, matchedBy, score));
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                var dir = Path.GetDirectoryName(item.Path);
                var stem = Path.GetFileNameWithoutExtension(item.Path);
                if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(stem))
                {
                    foreach (var ext in new[] { ".srt", ".vtt", ".en.srt", ".eng.srt" })
                    {
                        var direct = Path.Combine(dir, stem + ext);
                        Consider(direct, "stem", 80);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct sidecar probe failed for {Item}", item.Name);
        }

        try
        {
            IEnumerable<MediaStream> streams;
            try
            {
                streams = item.GetMediaStreams();
            }
            catch
            {
                streams = _mediaSourceManager.GetMediaStreams(item.Id);
            }

            foreach (var stream in streams)
            {
                if (stream.Type != MediaStreamType.Subtitle || !stream.IsExternal || string.IsNullOrWhiteSpace(stream.Path))
                {
                    continue;
                }

                var ext = Path.GetExtension(stream.Path).ToLowerInvariant();
                if (ext is not (".srt" or ".vtt"))
                {
                    continue;
                }

                var matchedBy = !string.IsNullOrWhiteSpace(mediaBase)
                                && Path.GetFileNameWithoutExtension(stream.Path)
                                    .Contains(mediaBase, StringComparison.OrdinalIgnoreCase)
                    ? "stem"
                    : "folder";
                Consider(stream.Path, matchedBy, 40 + ScoreSubtitleStream(stream, preferred));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetMediaStreams failed for {Item}", item.Name);
        }

        try
        {
            var folder = item.ContainingFolderPath;
            if (string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(item.Path))
            {
                folder = Path.GetDirectoryName(item.Path);
            }

            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".srt" or ".vtt"))
                    {
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(file);
                    var score = 0;
                    var matchedBy = "folder";
                    if (!string.IsNullOrWhiteSpace(mediaBase)
                        && name.Contains(mediaBase, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 50;
                        matchedBy = "stem";
                    }

                    foreach (var lang in preferred)
                    {
                        var n = name.ToLowerInvariant();
                        if (n.Contains($".{lang}", StringComparison.Ordinal)
                            || n.EndsWith($"_{lang}", StringComparison.Ordinal)
                            || n.EndsWith($"-{lang}", StringComparison.Ordinal)
                            || n.Contains($".{ExpandLang(lang)}", StringComparison.Ordinal))
                        {
                            score += 5;
                        }
                    }

                    if (ext == ".srt")
                    {
                        score += 1;
                    }

                    // Season-generic (lang only, no stem/SxxExx): keep very low so stem wins.
                    if (matchedBy == "folder" && ScoreEpisodeAlignment(name, season, episode) == 0
                        && season is not null)
                    {
                        score -= 20;
                    }

                    Consider(file, matchedBy, score);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Folder subtitle scan failed for {Item}", item.Name);
        }

        return candidates
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .Select(c => ((string Path, string MatchedBy, int Score)?)(c.Path, c.MatchedBy, c.Score))
            .FirstOrDefault();
    }

    private static int ScoreEpisodeAlignment(string fileName, int? season, int? episode)
    {
        if (season is null || episode is null)
        {
            return 0;
        }

        var m = SeasonEpisodeRegex.Match(fileName);
        if (!m.Success)
        {
            // No SxxExx in filename — ambiguous for TV.
            return -15;
        }

        if (!int.TryParse(m.Groups["s"].Value, out var s)
            || !int.TryParse(m.Groups["e"].Value, out var e))
        {
            return -15;
        }

        if (s == season && e == episode)
        {
            return 40;
        }

        // Wrong episode — hard reject via large penalty.
        return -200;
    }

    private static bool IsTextSubtitle(MediaStream stream)
    {
        if (stream.IsTextSubtitleStream)
        {
            return true;
        }

        var codec = stream.Codec ?? string.Empty;
        return TextSubtitleCodecs.Contains(codec);
    }

    private static int ScoreSubtitleStream(MediaStream stream, HashSet<string> preferred)
    {
        var score = 0;
        var lang = NormalizeLang(stream.Language);
        if (!string.IsNullOrEmpty(lang) && preferred.Contains(lang))
        {
            score += 20;
        }

        if (stream.IsDefault)
        {
            score += 5;
        }

        if (stream.IsForced)
        {
            score -= 10;
        }

        if (stream.IsHearingImpaired)
        {
            score -= 2;
        }

        var codec = stream.Codec ?? string.Empty;
        if (codec.Equals("subrip", StringComparison.OrdinalIgnoreCase)
            || codec.Equals("srt", StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        return score;
    }

    private static string NormalizeLang(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        var lang = language.Trim().ToLowerInvariant();
        return lang switch
        {
            "eng" => "en",
            "fre" or "fra" => "fr",
            "ger" or "deu" => "de",
            "spa" => "es",
            "ita" => "it",
            "jpn" => "ja",
            "chi" or "zho" => "zh",
            _ => lang.Length > 2 ? lang[..2] : lang
        };
    }

    private static string ExpandLang(string lang) => lang switch
    {
        "en" => "eng",
        "fr" => "fre",
        "de" => "ger",
        "es" => "spa",
        _ => lang
    };

    private sealed record SubtitleContent(
        string Content,
        string Label,
        string Source,
        string MatchedBy,
        string? MovieHash = null);
}
