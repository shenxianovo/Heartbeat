using Heartbeat.Core.DTOs;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Data;
using Heartbeat.Server.Extensions;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/usage")]
    public class UsageController(UsageService service, AppDbContext db) : ControllerBase
    {
        private readonly UsageService _service = service;

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> Upload([FromBody] UsageUploadRequest request)
        {
            if (request.Usages == null || request.Usages.Count == 0)
                return BadRequest("Usages cannot be empty.");

            var device = await this.ResolveDeviceAsync(db);
            if (device == null)
                return BadRequest($"Missing {DeviceResolverExtensions.DeviceNameHeader} header.");

            await _service.SaveUsageAsync(device.Id, request);
            return Ok();
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<AppUsageResponse>), 200)]
        public async Task<IActionResult> GetUsage(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var result = await _service.GetUsageAsync(deviceId, start, end);
            return Ok(result);
        }
    }
}
