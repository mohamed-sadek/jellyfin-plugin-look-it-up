using System.Reflection;
using Jellyfin.Plugin.LookItUp.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LookItUp.Controllers;

/// <summary>
/// API endpoints for Look it up annotations and the web overlay script.
/// </summary>
[ApiController]
[Route("LookItUp")]
public class LookItUpController : ControllerBase
{
    private readonly ILookItUpService _lookItUpService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LookItUpController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LookItUpController"/> class.
    /// </summary>
    public LookItUpController(
        ILookItUpService lookItUpService,
        ILibraryManager libraryManager,
        ILogger<LookItUpController> logger)
    {
        _lookItUpService = lookItUpService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets timed annotations for a media item.
    /// </summary>
    [HttpGet("{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
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

            var annotations = await _lookItUpService
                .GetAnnotationsAsync(itemId, force, cancellationToken)
                .ConfigureAwait(false);

            var config = Plugin.Instance?.Configuration;
            return Ok(new
            {
                itemId,
                itemName = item.Name,
                enabled = config?.Enabled ?? false,
                popupDurationMs = config?.PopupDurationMs ?? 5000,
                count = annotations.Count,
                annotations
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
    /// Forces a subtitle rescan for a media item.
    /// </summary>
    [HttpPost("{itemId}/scan")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Rescan(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_libraryManager.GetItemById(itemId) is null)
            {
                return NotFound();
            }

            var annotations = await _lookItUpService
                .GetAnnotationsAsync(itemId, forceRescan: true, cancellationToken)
                .ConfigureAwait(false);

            return Ok(new { itemId, count = annotations.Count, annotations });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LookItUp scan failed for {ItemId}", itemId);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = ex.Message,
                type = ex.GetType().Name,
                itemId
            });
        }
    }

    /// <summary>
    /// Health/debug endpoint.
    /// </summary>
    [HttpGet("status")]
    [Authorize]
    public ActionResult GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            plugin = Plugin.Instance?.Name,
            version = Plugin.Instance?.Version?.ToString(),
            enabled = config?.Enabled ?? false,
            wikipediaLanguage = config?.WikipediaLanguage,
            instanceLoaded = Plugin.Instance is not null
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
