using Heartbeat.Core.DTOs;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/usage")]
    public class UsageController(UsageService usageService, DeviceService deviceService) : ControllerBase
    {
        private readonly UsageService _usageService = usageService;
        private readonly DeviceService _deviceService = deviceService;

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> Upload([FromBody] UsageUploadRequest request)
        {
            if (request.Usages == null || request.Usages.Count == 0)
                return BadRequest("Usages cannot be empty.");

            var rawHeader = Request.Headers[DeviceService.DeviceNameHeader].FirstOrDefault();
            var device = await _deviceService.ResolveByNameAsync(rawHeader);
            if (device == null)
                return BadRequest($"Missing {DeviceService.DeviceNameHeader} header.");

            await _usageService.SaveUsageAsync(device.Id, request);
            return Ok();
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<AppUsageResponse>), 200)]
        public async Task<IActionResult> GetUsage(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var result = await _usageService.GetUsageAsync(deviceId, start, end);
            return Ok(result);
        }
    }
}
