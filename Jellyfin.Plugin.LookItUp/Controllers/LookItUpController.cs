using System.Reflection;
using Jellyfin.Plugin.LookItUp.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>
    /// Initializes a new instance of the <see cref="LookItUpController"/> class.
    /// </summary>
    /// <param name="lookItUpService">Look it up service.</param>
    /// <param name="libraryManager">Library manager.</param>
    public LookItUpController(ILookItUpService lookItUpService, ILibraryManager libraryManager)
    {
        _lookItUpService = lookItUpService;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets timed annotations for a media item.
    /// </summary>
    /// <param name="itemId">Media item id.</param>
    /// <param name="force">Force a rescan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Annotation payload.</returns>
    [HttpGet("{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetAnnotations(
        [FromRoute] Guid itemId,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (_libraryManager.GetItemById(itemId) is null)
        {
            return NotFound();
        }

        var annotations = await _lookItUpService
            .GetAnnotationsAsync(itemId, force, cancellationToken)
            .ConfigureAwait(false);

        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            itemId,
            enabled = config?.Enabled ?? false,
            popupDurationMs = config?.PopupDurationMs ?? 5000,
            annotations
        });
    }

    /// <summary>
    /// Forces a subtitle rescan for a media item.
    /// </summary>
    /// <param name="itemId">Media item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Fresh annotations.</returns>
    [HttpPost("{itemId}/scan")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Rescan(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Serves the web client overlay script.
    /// </summary>
    /// <returns>JavaScript payload.</returns>
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
