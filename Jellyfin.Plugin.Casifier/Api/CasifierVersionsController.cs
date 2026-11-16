using System.Net.Mime;
using Jellyfin.Plugin.Casifier.Models;
using Jellyfin.Plugin.Casifier.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Casifier.Api;

/// <summary>Lists movies with multiple versions and deletes lower versions. Administrators only.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Casifier/Versions")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class CasifierVersionsController : ControllerBase
{
    private readonly MovieVersionService _versions;

    public CasifierVersionsController(MovieVersionService versions)
    {
        _versions = versions;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<VersionReport> GetVersions() => _versions.GetReport();

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
