using Jellyfin.Plugin.LookItUp.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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
/// Builds timed Wikipedia annotations from a media item's external subtitles.
/// </summary>
public class LookItUpService : ILookItUpService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
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
        ISubtitleParser subtitleParser,
        IEntityExtractor entityExtractor,
        IWikipediaLookupService wikipedia,
        IAnnotationStore store,
        ILogger<LookItUpService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
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
                if (cached is not null)
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

            var subtitlePath = FindSubtitlePath(item, config.PreferredSubtitleLanguages);
            if (subtitlePath is null)
            {
                _logger.LogInformation("No external SRT/VTT subtitles found for {Item}", item.Name);
                SaveEmpty(itemId);
                return Array.Empty<ContextAnnotation>();
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(subtitlePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read subtitle file {Path}", subtitlePath);
                SaveEmpty(itemId);
                return Array.Empty<ContextAnnotation>();
            }

            var cues = _subtitleParser.Parse(content, subtitlePath);
            var annotations = new List<ContextAnnotation>();
            var usedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var max = Math.Max(1, config.MaxAnnotationsPerItem);

            foreach (var cue in cues)
            {
                if (annotations.Count >= max)
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
                    if (annotations.Count >= max || !usedTerms.Add(entity))
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

                    var popupMs = Math.Max(config.PopupDurationMs, 2000);
                    annotations.Add(new ContextAnnotation
                    {
                        Term = lookup.Title,
                        Summary = $"{lookup.Title}: {lookup.Summary}",
                        Url = lookup.Url,
                        StartMs = cue.StartMs,
                        EndMs = Math.Max(cue.EndMs, cue.StartMs + popupMs)
                    });

                    _logger.LogInformation(
                        "Look it up matched {Term} at {StartMs}ms in {Item}",
                        lookup.Title,
                        cue.StartMs,
                        item.Name);
                }
            }

            var cache = new ItemAnnotationCache
            {
                ItemId = itemId,
                ScannedAtUtc = DateTime.UtcNow,
                SubtitlePath = subtitlePath,
                Annotations = annotations
            };
            _store.Save(cache);

            _logger.LogInformation(
                "Look it up scanned {Item}: {Count} annotations from {Subtitle}",
                item.Name,
                annotations.Count,
                subtitlePath);

            return annotations;
        }
        catch (Exception ex)
        {
            // Never bubble to a hard 500 — return empty so playback keeps working.
            _logger.LogError(ex, "Look it up failed for item {ItemId}: {Message}", itemId, ex.Message);
            return Array.Empty<ContextAnnotation>();
        }
    }

    private void SaveEmpty(Guid itemId)
    {
        _store.Save(new ItemAnnotationCache
        {
            ItemId = itemId,
            ScannedAtUtc = DateTime.UtcNow,
            Annotations = []
        });
    }

    private string? FindSubtitlePath(BaseItem item, string preferredLanguages)
    {
        var preferred = (preferredLanguages ?? "en")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToLowerInvariant())
            .ToHashSet();

        var candidates = new List<(string Path, int Score)>();

        // 1) External streams indexed by Jellyfin (BaseItem first — more stable across versions)
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

                var score = 30;
                if (!string.IsNullOrWhiteSpace(stream.Language)
                    && preferred.Contains(stream.Language.ToLowerInvariant()))
                {
                    score += 5;
                }

                candidates.Add((stream.Path, score));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetMediaStreams failed for {Item}", item.Name);
        }

        // 2) Sidecar files next to the media
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
                            || name.EndsWith($"-{lang}", StringComparison.Ordinal))
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

        // 3) Same-name sidecar beside the video file
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
}
