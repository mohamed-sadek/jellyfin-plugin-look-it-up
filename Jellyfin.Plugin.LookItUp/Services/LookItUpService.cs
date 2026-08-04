using Jellyfin.Plugin.LookItUp.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
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
    /// <param name="itemId">Media item id.</param>
    /// <param name="forceRescan">Whether to ignore cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Annotations for playback.</returns>
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
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
        {
            return [];
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
            return [];
        }

        var subtitlePath = FindSubtitlePath(item, config.PreferredSubtitleLanguages, _mediaSourceManager);
        if (subtitlePath is null)
        {
            _logger.LogInformation("No external SRT/VTT subtitles found for {Item}", item.Name);
            var empty = new ItemAnnotationCache
            {
                ItemId = itemId,
                ScannedAtUtc = DateTime.UtcNow,
                Annotations = []
            };
            _store.Save(empty);
            return [];
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(subtitlePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read subtitle file {Path}", subtitlePath);
            return [];
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

            var entities = _entityExtractor.Extract(cue.Text, config.MinEntityLength);
            foreach (var entity in entities)
            {
                if (annotations.Count >= max || !usedTerms.Add(entity))
                {
                    continue;
                }

                var lookup = await _wikipedia
                    .LookupAsync(entity, config.WikipediaLanguage, cancellationToken)
                    .ConfigureAwait(false);

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

    private static string? FindSubtitlePath(
        BaseItem item,
        string preferredLanguages,
        IMediaSourceManager mediaSourceManager)
    {
        var preferred = preferredLanguages
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToLowerInvariant())
            .ToHashSet();

        var folder = item.ContainingFolderPath;
        if (string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(item.Path))
        {
            folder = Path.GetDirectoryName(item.Path);
        }

        var candidates = new List<(string Path, int Score)>();

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
                if (!string.IsNullOrWhiteSpace(item.Path)
                    && name.Contains(Path.GetFileNameWithoutExtension(item.Path), StringComparison.OrdinalIgnoreCase))
                {
                    score += 10;
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

        foreach (var stream in mediaSourceManager.GetMediaStreams(item.Id)
                     .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal))
        {
            if (string.IsNullOrWhiteSpace(stream.Path))
            {
                continue;
            }

            var ext = Path.GetExtension(stream.Path).ToLowerInvariant();
            if (ext is not (".srt" or ".vtt"))
            {
                continue;
            }

            var score = 20;
            if (!string.IsNullOrWhiteSpace(stream.Language)
                && preferred.Contains(stream.Language.ToLowerInvariant()))
            {
                score += 5;
            }

            candidates.Add((stream.Path, score));
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .Select(c => c.Path)
            .FirstOrDefault(File.Exists);
    }
}
