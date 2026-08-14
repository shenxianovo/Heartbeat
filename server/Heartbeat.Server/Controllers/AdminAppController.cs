using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers;

[ApiController]
[Route("api/v1/admin/apps")]
[Authorize]
public class AdminAppController(
    AppMergeService mergeService,
    AdminAuthorizationService adminAuthorization,
    ICurrentUserService currentUser) : ControllerBase
{
    [HttpPost("merge")]
    [EndpointName("mergeApps")]
    public async Task<ActionResult<AppMergeResponse>> Merge(
        [FromBody] AppMergeRequest request,
        CancellationToken cancellationToken)
    {
        if (!adminAuthorization.IsAdmin(currentUser.GetUserId())) return Forbid();

        try
        {
            return await mergeService.MergeAsync(request, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new AppCatalogAdminErrorResponse
            {
                Code = "invalid_request",
                Message = ex.Message
            });
        }
        catch (AppMergeException ex) when (ex.Code.EndsWith("_not_found", StringComparison.Ordinal))
        {
            return NotFound(new AppCatalogAdminErrorResponse { Code = ex.Code, Message = ex.Message });
        }
        catch (AppMergeException ex)
        {
            return Conflict(new AppCatalogAdminErrorResponse { Code = ex.Code, Message = ex.Message });
        }
    }
}
