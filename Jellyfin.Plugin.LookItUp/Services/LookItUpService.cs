using System.Text;
using Jellyfin.Plugin.LookItUp.Models;
using MediaBrowser.Controller.Entities;
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
    /// Gets annotations for an item, scanning when needed.
    /// </summary>
    Task<IReadOnlyList<ContextAnnotation>> GetAnnotationsAsync(
        Guid itemId,
        bool forceRescan,
        CancellationToken cancellationToken);
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
    private const int CacheVersion = 3;

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
        IAnnotationStore store,
        ILogger<LookItUpService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _subtitleEncoder = subtitleEncoder;
        _subtitleParser = subtitleParser;
        _entityExtractor = entityExtractor;
        _wikipedia = wikipedia;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextAnnotation>> GetAnnotationsAsync(
        Guid itemId,
        bool forceRescan,
        CancellationToken cancellationToken)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled)
            {
                return Array.Empty<ContextAnnotation>();
            }

            if (!forceRescan)
            {
                var cached = _store.Get(itemId);
                if (cached is not null && cached.Version >= CacheVersion)
                {
                    return cached.Annotations;
                }
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                _logger.LogWarning("Look it up: item {ItemId} not found", itemId);
                return Array.Empty<ContextAnnotation>();
            }

            var subtitle = await ResolveSubtitleContentAsync(item, config.PreferredSubtitleLanguages, cancellationToken)
                .ConfigureAwait(false);
            if (subtitle is null)
            {
                _logger.LogInformation("No readable text subtitles found for {Item}", item.Name);
                SaveEmpty(itemId, null);
                return Array.Empty<ContextAnnotation>();
            }

            var cues = _subtitleParser.Parse(subtitle.Content, subtitle.Label);
            var candidates = new List<(ContextAnnotation Annotation, int Score)>();
            var usedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var max = Math.Max(1, config.MaxAnnotationsPerItem);

            foreach (var cue in cues)
            {
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
                    // Multi-word names (Jon Voight) get a longer on-screen window.
                    var popupMs = Math.Max(config.PopupDurationMs, score >= 30 ? 8000 : 4000);
                    var annotation = new ContextAnnotation
                    {
                        Term = lookup.Title,
                        Summary = $"{lookup.Title}: {lookup.Summary}",
                        Url = lookup.Url,
                        StartMs = cue.StartMs,
                        EndMs = Math.Max(cue.EndMs, cue.StartMs + popupMs)
                    };

                    candidates.Add((annotation, score));
                    _logger.LogInformation(
                        "Look it up matched {Term} at {StartMs}ms (score {Score}) in {Item}",
                        lookup.Title,
                        cue.StartMs,
                        score,
                        item.Name);
                }
            }

            // Keep the best names overall, then play them in timeline order.
            var annotations = candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Annotation.StartMs)
                .Take(max)
                .Select(c => c.Annotation)
                .OrderBy(a => a.StartMs)
                .ToList();

            _store.Save(new ItemAnnotationCache
            {
                ItemId = itemId,
                Version = CacheVersion,
                ScannedAtUtc = DateTime.UtcNow,
                SubtitlePath = subtitle.Label,
                Annotations = annotations
            });

            _logger.LogInformation(
                "Look it up scanned {Item}: {Count} annotations from {Subtitle}",
                item.Name,
                annotations.Count,
                subtitle.Label);

            return annotations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Look it up failed for item {ItemId}: {Message}", itemId, ex.Message);
            return Array.Empty<ContextAnnotation>();
        }
    }

    private void SaveEmpty(Guid itemId, string? label)
    {
        _store.Save(new ItemAnnotationCache
        {
            ItemId = itemId,
            Version = CacheVersion,
            ScannedAtUtc = DateTime.UtcNow,
            SubtitlePath = label,
            Annotations = []
        });
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
