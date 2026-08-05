using System.Reflection;
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
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                return NotFound(new { error = "Item not found", itemId });
            }

            var prepared = _lookItUpService.TryGetPrepared(itemId, out var cache);
            var annotations = await _lookItUpService
                .GetAnnotationsAsync(itemId, force, cancellationToken)
                .ConfigureAwait(false);

            var config = Plugin.Instance?.Configuration;
            return Ok(new
            {
                itemId,
                itemName = item.Name,
                enabled = config?.Enabled ?? false,
                prepared = prepared || annotations.Count > 0,
                preparedAtUtc = cache?.ScannedAtUtc,
                cacheVersion = cache?.Version ?? 0,
                popupDurationMs = config?.PopupDurationMs ?? 3000,
                popup = BuildPopupSettings(config),
                count = annotations.Count,
                annotations,
                hint = prepared || annotations.Count > 0
                    ? null
                    : "No prepared annotations. Run Look it up library prepare (Dashboard → Scheduled Tasks or plugin page)."
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

    private static object BuildPopupSettings(Configuration.PluginConfiguration? config)
    {
        return new
        {
            durationMs = Math.Clamp(config?.PopupDurationMs ?? 3000, 1000, 30000),
            fontSizePx = Math.Clamp(config?.PopupFontSizePx ?? 16, 10, 48),
            textColor = string.IsNullOrWhiteSpace(config?.PopupTextColor) ? "#f7fafc" : config!.PopupTextColor.Trim(),
            backgroundColor = string.IsNullOrWhiteSpace(config?.PopupBackgroundColor)
                ? "rgba(8, 12, 20, 0.96)"
                : config!.PopupBackgroundColor.Trim(),
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
    /// Starts a background library prepare job.
    /// </summary>
    [HttpPost("prepare")]
    [Authorize]
    public ActionResult StartLibraryPrepare([FromQuery] bool force = false)
    {
        var started = _prepareService.TryStartLibraryPrepare(force);
        return Ok(new
        {
            started,
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
            wikipediaLanguage = config?.WikipediaLanguage,
            scanOnPlayback = config?.ScanOnPlayback ?? false,
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
            prepare = _prepareService.GetStatus(),
            instanceLoaded = Plugin.Instance is not null,
            targetServer = "10.11.x"
        });
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
        return Content(script, "application/javascript");
    }
}
