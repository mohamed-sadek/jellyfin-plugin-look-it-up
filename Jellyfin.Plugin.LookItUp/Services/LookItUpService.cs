using System.Text;
using Jellyfin.Plugin.LookItUp.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
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
    Task<PrepareItemResult> PrepareItemAsync(
        Guid itemId,
        bool force,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns a prepared cache entry when present and current.
    /// </summary>
    bool TryGetPrepared(Guid itemId, out ItemAnnotationCache? cache);

    /// <summary>
    /// Dry-run: extract subtitle name candidates that would be sent to AI (no AI call).
    /// </summary>
    Task<NameCandidatesResult> GetNameCandidatesAsync(Guid itemId, CancellationToken cancellationToken);
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
    public const int CurrentCacheVersion = 6;

    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text", "text", "microdvd", "mpl2", "sami", "stl", "ttml", "dfxp"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly ISubtitleParser _subtitleParser;
    private readonly IEntityExtractor _entityExtractor;
    private readonly IWikipediaLookupService _wikipedia;
    private readonly IAiEntityExtractor _aiExtractor;
    private readonly INameCandidateFinder _nameCandidateFinder;
    private readonly IAnnotationStore _store;
    private readonly ILogger<LookItUpService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookItUpService"/> class.
    /// </summary>
    public LookItUpService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ISubtitleEncoder subtitleEncoder,
        ISubtitleParser subtitleParser,
        IEntityExtractor entityExtractor,
        IWikipediaLookupService wikipedia,
        IAiEntityExtractor aiExtractor,
        INameCandidateFinder nameCandidateFinder,
        IAnnotationStore store,
        ILogger<LookItUpService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _subtitleEncoder = subtitleEncoder;
        _subtitleParser = subtitleParser;
        _entityExtractor = entityExtractor;
        _wikipedia = wikipedia;
        _aiExtractor = aiExtractor;
        _nameCandidateFinder = nameCandidateFinder;
        _store = store;
        _logger = logger;
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
            return cached.Annotations;
        }

        // Playback should usually hit precomputed data. On-demand prepare is opt-in.
        if (!forceRescan && !config.ScanOnPlayback)
        {
            _logger.LogDebug("No prepared annotations for {ItemId}; run library prepare", itemId);
            return Array.Empty<ContextAnnotation>();
        }

        var prepared = await PrepareItemAsync(itemId, force: true, cancellationToken)
            .ConfigureAwait(false);
        return (IReadOnlyList<ContextAnnotation>)(prepared.Cache?.Annotations ?? []);
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
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load series cast for {Item}", item.Name);
            }
        }

        return exclude;
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
        CancellationToken cancellationToken)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled)
            {
                return new PrepareItemResult { Warning = "Plugin disabled." };
            }

            if (!force && TryGetPrepared(itemId, out var existing) && existing is not null)
            {
                return new PrepareItemResult { Cache = existing, Mode = "cache" };
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                _logger.LogWarning("Look it up prepare: item {ItemId} not found", itemId);
                return new PrepareItemResult { Warning = "Item not found." };
            }

            var subtitle = await ResolveSubtitleContentAsync(item, config.PreferredSubtitleLanguages, cancellationToken)
                .ConfigureAwait(false);
            if (subtitle is null)
            {
                _logger.LogInformation("No readable text subtitles found for {Item}", item.Name);
                var empty = SaveCache(itemId, null, []);
                MaybeWriteSidecar(item, empty, config.WriteSidecarFiles);
                return new PrepareItemResult
                {
                    Cache = empty,
                    Mode = "none",
                    Warning = "No readable text subtitles found."
                };
            }

            var cues = _subtitleParser.Parse(subtitle.Content, subtitle.Label);
            var max = Math.Max(1, config.MaxAnnotationsPerItem);
            List<ContextAnnotation> annotations;
            string mode;
            string? warning = null;
            string? aiBaseUrl = null;
            string? aiModel = null;

            if (_aiExtractor.IsConfigured(config))
            {
                mode = "ai";
                aiModel = string.IsNullOrWhiteSpace(config.AiModel)
                    ? (string.Equals(config.AiProvider, "Groq", StringComparison.OrdinalIgnoreCase)
                       || (config.AiBaseUrl ?? string.Empty).Contains("groq.com", StringComparison.OrdinalIgnoreCase)
                        ? "llama-3.1-8b-instant"
                        : "gpt-4o-mini")
                    : config.AiModel.Trim();
                aiBaseUrl = OpenAiCompatibleEntityExtractor.ResolveBaseUrl(config, aiModel);

                var minLen = Math.Max(2, config.MinEntityLength);
                var excludedCast = BuildCastExcludeNames(item, minLen);
                var nameLimit = Math.Clamp(config.AiNamesPerPrepare, 1, 20);
                var nameCandidates = _nameCandidateFinder
                    .Find(cues, item.Name, excludedCast, minLen, Math.Max(max, nameLimit))
                    .Take(nameLimit)
                    .ToList();

                _logger.LogInformation(
                    "Look it up preparing {Item} with AI ({Provider}/{Model}) via {BaseUrl}: verifying top {Count} names",
                    item.Name,
                    config.AiProvider,
                    aiModel,
                    aiBaseUrl,
                    nameCandidates.Count);

                var aiResult = await _aiExtractor
                    .ResolveNamesAsync(
                        item.Name ?? itemId.ToString("N"),
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

                var popupMs = Math.Max(config.PopupDurationMs, 3000);
                annotations = aiResult.Mentions
                    .Select(m => new ContextAnnotation
                    {
                        Term = m.Term.Trim(),
                        Summary = m.Summary.Trim().StartsWith(m.Term, StringComparison.OrdinalIgnoreCase)
                            ? m.Summary.Trim()
                            : $"{m.Term.Trim()}: {m.Summary.Trim()}",
                        Url = null,
                        StartMs = m.StartMs,
                        EndMs = Math.Max(m.EndMs, m.StartMs + popupMs)
                    })
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

            var cache = SaveCache(itemId, subtitle.Label, annotations);
            MaybeWriteSidecar(item, cache, config.WriteSidecarFiles);

            _logger.LogInformation(
                "Look it up prepared {Item}: {Count} annotations from {Subtitle}",
                item.Name,
                annotations.Count,
                subtitle.Label);

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

    private ItemAnnotationCache SaveCache(Guid itemId, string? label, List<ContextAnnotation> annotations)
    {
        var cache = new ItemAnnotationCache
        {
            ItemId = itemId,
            Version = CurrentCacheVersion,
            ScannedAtUtc = DateTime.UtcNow,
            SubtitlePath = label,
            Annotations = annotations
        };
        _store.Save(cache);
        return cache;
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

        // 1) External sidecar / indexed external files
        var externalPath = FindExternalSubtitlePath(item, preferred);
        if (externalPath is not null)
        {
            try
            {
                var content = await File.ReadAllTextAsync(externalPath, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return new SubtitleContent(content, externalPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read subtitle file {Path}", externalPath);
            }
        }

        // 2) Embedded text tracks via Jellyfin's subtitle encoder (ffmpeg)
        return await ExtractEmbeddedSubtitleAsync(item, preferred, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SubtitleContent?> ExtractEmbeddedSubtitleAsync(
        BaseItem item,
        HashSet<string> preferred,
        CancellationToken cancellationToken)
    {
        try
        {
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
                try
                {
                    _logger.LogInformation(
                        "Extracting embedded subtitle #{Index} ({Codec}/{Lang}, score {Score}) for {Item}",
                        stream.Index,
                        stream.Codec,
                        stream.Language,
                        score,
                        item.Name);

                    await using var streamData = await _subtitleEncoder.GetSubtitles(
                            item,
                            mediaSource.Id,
                            stream.Index,
                            "srt",
                            startTimeTicks: 0,
                            endTimeTicks: 0,
                            preserveOriginalTimestamps: true,
                            cancellationToken)
                        .ConfigureAwait(false);

                    using var reader = new StreamReader(streamData, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    var label = $"embedded:{stream.Index}:{stream.Codec}:{stream.Language ?? "und"}";
                    return new SubtitleContent(content, label);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedded subtitle extraction failed for {Item}", item.Name);
        }

        return null;
    }

    private string? FindExternalSubtitlePath(BaseItem item, HashSet<string> preferred)
    {
        var candidates = new List<(string Path, int Score)>();

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

                candidates.Add((stream.Path, 30 + ScoreSubtitleStream(stream, preferred)));
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

                    var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    var score = 0;
                    if (!string.IsNullOrWhiteSpace(item.Path))
                    {
                        var mediaBase = Path.GetFileNameWithoutExtension(item.Path);
                        if (!string.IsNullOrWhiteSpace(mediaBase)
                            && name.Contains(mediaBase, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 10;
                        }
                    }

                    foreach (var lang in preferred)
                    {
                        if (name.Contains($".{lang}", StringComparison.Ordinal)
                            || name.EndsWith($"_{lang}", StringComparison.Ordinal)
                            || name.EndsWith($"-{lang}", StringComparison.Ordinal)
                            || name.Contains($".{ExpandLang(lang)}", StringComparison.Ordinal))
                        {
                            score += 5;
                        }
                    }

                    if (ext == ".srt")
                    {
                        score += 1;
                    }

                    candidates.Add((file, score));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Folder subtitle scan failed for {Item}", item.Name);
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
                        if (File.Exists(direct))
                        {
                            candidates.Add((direct, 40));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct sidecar probe failed for {Item}", item.Name);
        }

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Path))
            .OrderByDescending(c => c.Score)
            .Select(c => c.Path)
            .FirstOrDefault(path =>
            {
                try
                {
                    return File.Exists(path);
                }
                catch
                {
                    return false;
                }
            });
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

    private sealed record SubtitleContent(string Content, string Label);
}
