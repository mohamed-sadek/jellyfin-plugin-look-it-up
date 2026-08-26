using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Reflection;
using Jellyfin.Plugin.LookItUp.Models;
using Jellyfin.Plugin.LookItUp.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Controllers;

/// <summary>
/// API endpoints for Look it up annotations, prepare jobs, and the web overlay script.
/// </summary>
[ApiController]
[Route("LookItUp")]
public class LookItUpController : ControllerBase
{
    private static readonly HttpClient ImageClient = CreateImageClient();
    private static readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType, DateTimeOffset CachedAt)> ImageCache = new(StringComparer.Ordinal);

    private readonly ILookItUpService _lookItUpService;
    private readonly ILookItUpPrepareService _prepareService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LookItUpController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookItUpController"/> class.
    /// </summary>
    public LookItUpController(
        ILookItUpService lookItUpService,
        ILookItUpPrepareService prepareService,
        ILibraryManager libraryManager,
        ILogger<LookItUpController> logger)
    {
        _lookItUpService = lookItUpService;
        _prepareService = prepareService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets timed annotations for a media item (precomputed cache).
    /// </summary>
    [HttpGet("{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetAnnotations(
        [FromRoute] Guid itemId,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var item = LibraryItemResolver.GetItem(_libraryManager, itemId);
            if (item is null)
            {
                return NotFound(new
                {
                    error = "Item not found",
                    itemId,
                    hint = "Send the library ItemId (from NowPlayingItem), not MediaSourceId from the stream URL."
                });
            }

            var prepared = _lookItUpService.TryGetPrepared(item.Id, out var cache);
            var annotations = await _lookItUpService
                .GetAnnotationsAsync(item.Id, force, cancellationToken)
                .ConfigureAwait(false);

            var config = Plugin.Instance?.Configuration;
            var disabled = cache?.Disabled == true;
            return Ok(new
            {
                itemId = item.Id,
                itemName = item.Name,
                enabled = config?.Enabled ?? false,
                showPopupsDuringPlayback = config?.ShowPopupsDuringPlayback ?? true,
                prepared = prepared || annotations.Count > 0 || (cache?.Annotations.Count > 0),
                disabled,
                preparedAtUtc = cache?.ScannedAtUtc,
                preparedThroughMs = cache?.PreparedThroughMs ?? 0,
                fullyPrepared = cache?.FullyPrepared ?? false,
                incrementalPrepareOnPlayback = config?.IncrementalPrepareOnPlayback ?? false,
                incrementalPrepareWindowMs = config?.IncrementalPrepareWindowMs ?? 300_000,
                incrementalAiNamesPerWindow = config?.IncrementalAiNamesPerWindow ?? 40,
                cacheVersion = cache?.Version ?? 0,
                subtitlePath = cache?.SubtitlePath,
                subtitleSource = cache?.SubtitleSource,
                matchedBy = cache?.MatchedBy,
                movieHash = cache?.MovieHash,
                durationCheckOk = cache?.DurationCheckOk,
                prepareOutcome = cache?.PrepareOutcome,
                annotationCount = cache?.Annotations.Count ?? 0,
                popupDurationMs = config?.PopupDurationMs ?? 3000,
                popup = BuildPopupSettings(config),
                count = annotations.Count,
                annotations,
                aiDecisions = config?.StoreAiDecisions == true ? cache?.AiDecisions : null,
                hint = prepared || annotations.Count > 0 || disabled
                    ? (disabled
                        ? "Popups disabled for this item."
                        : config?.ShowPopupsDuringPlayback == false
                            ? "Popups hidden globally — JSON cache still builds during playback."
                            : null)
                    : config?.IncrementalPrepareOnPlayback == true
                        ? "Preparing annotations during playback…"
                        : "No prepared annotations yet. Enable incremental prepare and play the item."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LookItUp GET failed for {ItemId}", itemId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = ex.Message,
                type = ex.GetType().Name,
                itemId
            });
        }
    }

    /// <summary>
    /// Incrementally prepares the next subtitle window during playback.
    /// </summary>
    [HttpPost("{itemId}/prepare-ahead")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PrepareAhead(
        [FromRoute] Guid itemId,
        [FromQuery] long playbackMs = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var item = LibraryItemResolver.GetItem(_libraryManager, itemId);
            if (item is null)
            {
                return NotFound(new
                {
                    error = "Item not found",
                    itemId,
                    hint = "Send the library ItemId (from NowPlayingItem), not MediaSourceId from the stream URL."
                });
            }

            var result = await _lookItUpService
                .PrepareAheadAsync(item.Id, playbackMs, cancellationToken)
                .ConfigureAwait(false);

            var config = Plugin.Instance?.Configuration;
            var cache = result.Cache;
            var annotations = cache?.Annotations ?? [];
            IReadOnlyList<AiVerifyDecision>? windowAiDecisions = null;
            if (config?.StoreAiDecisions == true
                && cache?.AiDecisions is { Count: > 0 } aiDecisions
                && result.Window is { } window)
            {
                windowAiDecisions = aiDecisions
                    .Where(d => d.StartMs >= window.FromMs && d.StartMs < window.ToMs)
                    .ToList();
            }

            return Ok(new
            {
                itemId = item.Id,
                resolvedFromMediaSourceId = item.Id != itemId,
                changed = result.Changed,
                mode = result.Mode,
                warning = result.Warning,
                playbackMs,
                preparedThroughMs = cache?.PreparedThroughMs ?? 0,
                fullyPrepared = cache?.FullyPrepared ?? false,
                subtitleDurationMs = result.SubtitleDurationMs,
                addedCount = result.Added.Count,
                added = result.Added,
                annotationCount = annotations.Count,
                annotations = _lookItUpService.TryGetPrepared(item.Id, out var prepared)
                    ? FilterPlaybackAnnotations(prepared!.Annotations)
                    : annotations,
                aiDecisions = config?.StoreAiDecisions == true ? cache?.AiDecisions : null,
                windowAiDecisions,
                showPopupsDuringPlayback = config?.ShowPopupsDuringPlayback ?? true,
                window = result.Window,
                popup = BuildPopupSettings(config)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LookItUp prepare-ahead failed for {ItemId}", itemId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = ex.Message,
                type = ex.GetType().Name,
                itemId
            });
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

    private static object BuildPopupSettings(Configuration.PluginConfiguration? config)
    {
        return new
        {
            durationMs = Math.Clamp(config?.PopupDurationMs ?? 3000, 1000, 30000),
            delayMs = Math.Clamp(config?.PopupDelayMs ?? 1000, 0, 10000),
            fontSizePx = Math.Clamp(config?.PopupFontSizePx ?? 16, 10, 48),
            textColor = string.IsNullOrWhiteSpace(config?.PopupTextColor) ? "#f7fafc" : config!.PopupTextColor.Trim(),
            borderColor = string.IsNullOrWhiteSpace(config?.PopupBorderColor) ? "#f1ff33" : config!.PopupBorderColor.Trim(),
            backgroundColor = string.IsNullOrWhiteSpace(config?.PopupBackgroundColor)
                ? "rgba(8, 12, 20, 0.96)"
                : config!.PopupBackgroundColor.Trim(),
            scaleWithScreen = config?.PopupScaleWithScreen ?? true,
            placement = string.IsNullOrWhiteSpace(config?.PopupPlacement) ? "BottomCenter" : config!.PopupPlacement.Trim(),
            edgeOffsetPct = Math.Clamp(config?.PopupEdgeOffsetPct ?? 10, 2, 40)
        };
    }

    /// <summary>
    /// Dry-run name finding for an item (no AI). Returns candidates that would be sent to Groq.
    /// </summary>
    [HttpGet("{itemId}/candidates")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetNameCandidates(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_libraryManager.GetItemById(itemId) is null)
            {
                return NotFound(new { error = "Item not found", itemId });
            }

            var result = await _lookItUpService
                .GetNameCandidatesAsync(itemId, cancellationToken)
                .ConfigureAwait(false);

            return Ok(new
            {
                itemId = result.ItemId,
                itemName = result.ItemName,
                subtitle = result.Subtitle,
                cueCount = result.CueCount,
                count = result.Candidates.Count,
                warning = result.Warning,
                excludedCastCount = result.ExcludedCastNames.Count,
                excludedCastSample = result.ExcludedCastNames.Take(30),
                candidates = result.Candidates.Select(c => new
                {
                    term = c.Term,
                    startMs = c.StartMs,
                    endMs = c.EndMs,
                    cueText = c.CueText,
                    score = c.Score,
                    reason = c.Reason,
                    midSentenceHits = c.MidSentenceHits,
                    start = TimeSpan.FromMilliseconds(c.StartMs).ToString(@"hh\:mm\:ss\.fff")
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LookItUp candidates failed for {ItemId}", itemId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = ex.Message,
                type = ex.GetType().Name,
                itemId
            });
        }
    }

    /// <summary>
    /// Disables popups for an item without deleting prepared annotations.
    /// </summary>
    [HttpPost("{itemId}/disable")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DisableItem([FromRoute] Guid itemId)
    {
        if (!_lookItUpService.TrySetDisabled(itemId, disabled: true, out var cache))
        {
            return NotFound(new { error = "No prepared annotations for this item", itemId });
        }

        return Ok(new { itemId, disabled = true, count = cache?.Annotations.Count ?? 0 });
    }

    /// <summary>
    /// Re-enables popups for an item.
    /// </summary>
    [HttpPost("{itemId}/enable")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult EnableItem([FromRoute] Guid itemId)
    {
        if (!_lookItUpService.TrySetDisabled(itemId, disabled: false, out var cache))
        {
            return NotFound(new { error = "No prepared annotations for this item", itemId });
        }

        return Ok(new { itemId, disabled = false, count = cache?.Annotations.Count ?? 0 });
    }

    /// <summary>
    /// Forces prepare/rescan for a single media item.
    /// </summary>
    [HttpPost("{itemId}/prepare")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PrepareItem(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_libraryManager.GetItemById(itemId) is null)
            {
                return NotFound();
            }

            var result = await _prepareService
                .PrepareItemAsync(itemId, force: true, cancellationToken)
                .ConfigureAwait(false);

            var cache = result.Cache;
            return Ok(new
            {
                itemId,
                count = cache?.Annotations.Count ?? 0,
                preparedAtUtc = cache?.ScannedAtUtc,
                subtitle = cache?.SubtitlePath,
                subtitleSource = cache?.SubtitleSource,
                matchedBy = cache?.MatchedBy,
                durationCheckOk = cache?.DurationCheckOk,
                prepareOutcome = cache?.PrepareOutcome,
                disabled = cache?.Disabled ?? false,
                mode = result.Mode,
                aiBaseUrl = result.AiBaseUrl,
                aiModel = result.AiModel,
                warning = result.Warning,
                annotations = cache?.Annotations ?? []
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LookItUp prepare failed for {ItemId}", itemId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = ex.Message,
                type = ex.GetType().Name,
                itemId
            });
        }
    }

    /// <summary>
    /// Legacy alias for single-item prepare.
    /// </summary>
    [HttpPost("{itemId}/scan")]
    [Authorize]
    public Task<ActionResult> Rescan(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken = default)
        => PrepareItem(itemId, cancellationToken);

    /// <summary>
    /// Starts a background prepare for a Series (all episodes), Season, Episode, or Movie.
    /// </summary>
    [HttpPost("{itemId}/prepare-series")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult PrepareSeriesOrSeason(
        [FromRoute] Guid itemId,
        [FromQuery] bool force = false)
    {
        if (_libraryManager.GetItemById(itemId) is null)
        {
            return NotFound(new { error = "Item not found", itemId });
        }

        if (!_prepareService.TryStartScopedPrepare(itemId, force, out var error))
        {
            var conflict = string.Equals(error, "A prepare job is already running.", StringComparison.Ordinal);
            return conflict
                ? Conflict(new { started = false, error, status = _prepareService.GetStatus() })
                : BadRequest(new { started = false, error, status = _prepareService.GetStatus() });
        }

        return Ok(new
        {
            started = true,
            itemId,
            force,
            status = _prepareService.GetStatus()
        });
    }

    /// <summary>
    /// Preview name candidates for a series/season/episode/movie (no AI).
    /// </summary>
    [HttpGet("{itemId}/prepare-preview")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetPreparePreview(
        [FromRoute] Guid itemId,
        [FromQuery] int? namesPerItem = null,
        CancellationToken cancellationToken = default)
    {
        if (_libraryManager.GetItemById(itemId) is null)
        {
            return NotFound(new { error = "Item not found", itemId });
        }

        var preview = await _lookItUpService
            .GetPreparePreviewAsync(itemId, namesPerItem, cancellationToken)
            .ConfigureAwait(false);
        return Ok(preview);
    }

    /// <summary>
    /// Starts a background prepare for user-selected terms per item.
    /// </summary>
    [HttpPost("{itemId}/prepare-selected")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult PrepareSelected(
        [FromRoute] Guid itemId,
        [FromBody] PrepareSelectedRequest? request)
    {
        // itemId is the root (show/episode) the UI was opened from; selections carry real episode ids.
        _ = itemId;
        request ??= new PrepareSelectedRequest();
        if (!_prepareService.TryStartSelectedPrepare(request, out var error))
        {
            var conflict = string.Equals(error, "A prepare job is already running.", StringComparison.Ordinal);
            return conflict
                ? Conflict(new { started = false, error, status = _prepareService.GetStatus() })
                : BadRequest(new { started = false, error, status = _prepareService.GetStatus() });
        }

        return Ok(new
        {
            started = true,
            status = _prepareService.GetStatus()
        });
    }

    /// <summary>
    /// Starts a background library prepare job (same work as the overnight scheduled task).
    /// </summary>
    [HttpPost("prepare")]
    [Authorize]
    public ActionResult StartLibraryPrepare([FromQuery] bool force = false)
    {
        var started = _prepareService.TryStartLibraryPrepare(force);
        return Ok(new
        {
            started,
            force,
            sameAsScheduledTask = true,
            status = _prepareService.GetStatus()
        });
    }

    /// <summary>
    /// Cancels a running library prepare job.
    /// </summary>
    [HttpPost("prepare/cancel")]
    [Authorize]
    public ActionResult CancelLibraryPrepare()
    {
        var cancelled = _prepareService.TryCancelLibraryPrepare();
        return Ok(new
        {
            cancelled,
            status = _prepareService.GetStatus()
        });
    }

    /// <summary>
    /// Deletes all generated Look it up files (caches, downloaded subs, sidecars, prepare queue).
    /// Does not change plugin settings or API keys.
    /// </summary>
    [HttpPost("clear-generated")]
    [Authorize]
    public ActionResult ClearGeneratedData()
    {
        var cancelled = _prepareService.TryCancelLibraryPrepare();
        ImageCache.Clear();
        var result = _lookItUpService.ClearGeneratedData();
        result.PrepareJobCancelled = cancelled;
        return Ok(new
        {
            ok = true,
            result.CacheFilesDeleted,
            result.SubtitleCacheFilesDeleted,
            result.OpenSubtitlesFilesDeleted,
            result.SidecarFilesDeleted,
            result.PrepareQueueCleared,
            result.PrepareJobCancelled,
            result.TotalFilesDeleted,
            status = _prepareService.GetStatus()
        });
    }

    /// <summary>
    /// Gets library prepare job progress.
    /// </summary>
    [HttpGet("prepare/status")]
    [Authorize]
    public ActionResult GetPrepareStatus()
        => Ok(_prepareService.GetStatus());

    /// <summary>
    /// Health/debug endpoint (no auth — safe to open in a browser tab).
    /// </summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public ActionResult GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            plugin = Plugin.Instance?.Name,
            version = Plugin.Instance?.Version?.ToString(),
            enabled = config?.Enabled ?? false,
            showPopupsDuringPlayback = config?.ShowPopupsDuringPlayback ?? true,
            wikipediaLanguage = config?.WikipediaLanguage,
            scanOnPlayback = config?.ScanOnPlayback ?? false,
            incrementalPrepareOnPlayback = config?.IncrementalPrepareOnPlayback ?? false,
            incrementalPrepareWindowMs = config?.IncrementalPrepareWindowMs ?? 300_000,
            incrementalAiNamesPerWindow = config?.IncrementalAiNamesPerWindow ?? 40,
            writeSidecarFiles = config?.WriteSidecarFiles ?? false,
            aiProvider = config?.AiProvider ?? "None",
            aiModel = config?.AiModel,
            aiResolvedModel = string.IsNullOrWhiteSpace(config?.AiApiKey)
                || string.Equals(config?.AiProvider, "None", StringComparison.OrdinalIgnoreCase)
                || config is null
                ? null
                : OpenAiCompatibleEntityExtractor.ResolveModel(config),
            aiBaseUrl = config?.AiBaseUrl,
            aiResolvedBaseUrl = string.IsNullOrWhiteSpace(config?.AiApiKey)
                || string.Equals(config?.AiProvider, "None", StringComparison.OrdinalIgnoreCase)
                || config is null
                ? null
                : OpenAiCompatibleEntityExtractor.ResolveBaseUrl(
                    config,
                    OpenAiCompatibleEntityExtractor.ResolveModel(config)),
            aiConfigured = !string.IsNullOrWhiteSpace(config?.AiApiKey)
                           && !string.Equals(config?.AiProvider, "None", StringComparison.OrdinalIgnoreCase),
            cacheVersion = _lookItUpService.CacheVersion,
            popup = BuildPopupSettings(config),
            prepare = _prepareService.GetStatus(),
            instanceLoaded = Plugin.Instance is not null,
            targetServer = "10.11.x"
        });
    }

    /// <summary>
    /// Proxies allow-listed Wikipedia thumbnails so the web overlay can load them under CSP img-src 'self'.
    /// </summary>
    [HttpGet("image")]
    [AllowAnonymous]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> GetImage([FromQuery] string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsAllowedImageHost(uri.Host))
        {
            return BadRequest(new { error = "url must be an https Wikipedia/Wikimedia image" });
        }

        var cacheKey = uri.AbsoluteUri;
        if (ImageCache.TryGetValue(cacheKey, out var cached)
            && DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromHours(24)
            && cached.Bytes.Length > 0)
        {
            return File(cached.Bytes, cached.ContentType);
        }

        try
        {
            using var response = await ImageClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "upstream was not an image" });
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > 2_000_000)
            {
                return BadRequest(new { error = "image size rejected" });
            }

            ImageCache[cacheKey] = (bytes, contentType, DateTimeOffset.UtcNow);
            if (ImageCache.Count > 64)
            {
                foreach (var stale in ImageCache
                             .OrderBy(kv => kv.Value.CachedAt)
                             .Take(16)
                             .Select(kv => kv.Key)
                             .ToList())
                {
                    ImageCache.TryRemove(stale, out _);
                }
            }

            return File(bytes, contentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Look it up image proxy failed for {Url}", uri);
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private static bool IsAllowedImageHost(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        return host == "upload.wikimedia.org"
               || host.EndsWith(".wikipedia.org", StringComparison.Ordinal)
               || host == "wikipedia.org"
               || host.EndsWith(".wikimedia.org", StringComparison.Ordinal);
    }

    private static HttpClient CreateImageClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Jellyfin.Plugin.LookItUp", "1.2"));
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("(+https://github.com/mohamed-sadek/jellyfin-plugin-look-it-up)"));
        return client;
    }

    /// <summary>
    /// Serves the web client overlay script.
    /// </summary>
    [HttpGet("script.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{typeof(Plugin).Namespace}.Web.lookitup.js";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-LookItUp-Version"] = Plugin.Instance?.Version?.ToString() ?? "unknown";
        return Content(script, "application/javascript");
    }
}
