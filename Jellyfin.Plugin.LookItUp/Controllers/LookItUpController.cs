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
                popupDurationMs = config?.PopupDurationMs ?? 2000,
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
            aiBaseUrl = config?.AiBaseUrl,
            aiResolvedBaseUrl = string.IsNullOrWhiteSpace(config?.AiApiKey)
                || string.Equals(config?.AiProvider, "None", StringComparison.OrdinalIgnoreCase)
                ? null
                : OpenAiCompatibleEntityExtractor.ResolveBaseUrl(
                    config!,
                    string.IsNullOrWhiteSpace(config?.AiModel) ? "gpt-4o-mini" : config!.AiModel),
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
