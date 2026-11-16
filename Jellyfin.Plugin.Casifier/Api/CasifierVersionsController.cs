using System.Net.Mime;
using Jellyfin.Plugin.Casifier.Models;
using Jellyfin.Plugin.Casifier.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Casifier.Api;

/// <summary>Lists movies with multiple versions and deletes lower versions. Administrators only.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Casifier/Versions")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class CasifierVersionsController : ControllerBase
{
    private readonly MovieVersionService _versions;
    private readonly ILogger<CasifierVersionsController> _logger;

    public CasifierVersionsController(MovieVersionService versions, ILogger<CasifierVersionsController> logger)
    {
        _versions = versions;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult<VersionReport> GetVersions()
    {
        try
        {
            return _versions.GetReport();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Casifier could not build the movie versions report");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = ex.Message });
        }
    }

    [HttpDelete("{movieId}/{versionId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<VersionDeleteResult> DeleteVersion([FromRoute] Guid movieId, [FromRoute] string versionId)
    {
        var outcome = _versions.DeleteVersion(movieId, versionId);
        return outcome.Result is not null
            ? outcome.Result
            : StatusCode(outcome.StatusCode, new { outcome.Message });
    }
}
