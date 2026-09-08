using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Filters;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers;

[ApiController]
[Route("api/v1/facts")]
[Authorize]
public sealed class FactController(FactStore store, ICurrentUserService currentUser) : ControllerBase
{
    [HttpPost]
    [EndpointName("uploadFacts")]
    [RequireHeartbeatProtocol]
    public async Task<IActionResult> Upload([FromBody] FactUploadRequest request, CancellationToken ct)
    {
        try { await store.IngestAsync(currentUser.GetUserId(), request, ct); }
        catch (FactIngestException ex) { return ex.IsConflict ? Conflict(ex.Message) : UnprocessableEntity(ex.Message); }
        catch (InputEventIngestContractException ex) { return UnprocessableEntity(ex.Message); }
        return Ok();
    }
}
