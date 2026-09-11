using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Filters;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers;

[ApiController]
[Route("api/v1/observations")]
[Authorize]
public sealed class ObservationController(FactStore store, ICurrentUserService currentUser) : ControllerBase
{
    [HttpPost]
    [EndpointName("uploadObservations")]
    [RequireHeartbeatProtocol]
    public async Task<IActionResult> Upload([FromBody] ObservationUploadRequest request, CancellationToken ct)
    {
        try { await store.IngestObservationsAsync(currentUser.GetUserId(), request, ct); }
        catch (FactIngestException ex) { return ex.IsConflict ? Conflict(ex.Message) : UnprocessableEntity(ex.Message); }
        return Ok();
    }
}
