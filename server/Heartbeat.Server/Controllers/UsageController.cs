using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Usage;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/usage")]
    [Authorize]
    public class UsageController(UsageService usageService, DeviceService deviceService, ICurrentUserService currentUser) : ControllerBase
    {
        private readonly UsageService _usageService = usageService;
        private readonly DeviceService _deviceService = deviceService;
        private readonly ICurrentUserService _currentUser = currentUser;

        [HttpPost]
        [EndpointName("uploadUsage")]
        public async Task<IActionResult> Upload([FromBody] UsageUploadRequest request)
        {
            if (request.Usages == null || request.Usages.Count == 0)
                return BadRequest("Usages cannot be empty.");

            var userId = _currentUser.GetUserId();
            var hardwareId = Request.Headers[DeviceService.HardwareIdHeader].FirstOrDefault();
            var deviceName = Request.Headers[DeviceService.DeviceNameHeader].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(hardwareId))
                return BadRequest($"Missing {DeviceService.HardwareIdHeader} header.");

            var device = await _deviceService.ResolveByHardwareIdAsync(userId, hardwareId, deviceName);
            await _usageService.SaveUsageAsync(device.Id, request);
            return Ok();
        }

        [HttpGet]
        [EndpointName("getUsage")]
        public async Task<ActionResult<List<AppUsageResponse>>> GetUsage(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var userId = _currentUser.GetUserId();
            return await _usageService.GetUsageAsync(userId, deviceId, start, end);
        }
    }
}
