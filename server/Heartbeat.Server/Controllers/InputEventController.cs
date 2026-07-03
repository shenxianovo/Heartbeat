using Heartbeat.Core.DTOs.Input;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/input-events")]
    [Authorize]
    public class InputEventController(
        InputEventService inputEventService,
        DeviceService deviceService,
        ICurrentUserService currentUser) : ControllerBase
    {
        private readonly InputEventService _inputEventService = inputEventService;
        private readonly DeviceService _deviceService = deviceService;
        private readonly ICurrentUserService _currentUser = currentUser;

        [HttpPost]
        [EndpointName("uploadInputEvents")]
        public async Task<IActionResult> Upload([FromBody] InputEventUploadRequest request)
        {
            if (request.Events == null || request.Events.Count == 0)
                return BadRequest("Events cannot be empty.");

            var userId = _currentUser.GetUserId();
            var hardwareId = Request.Headers[DeviceService.HardwareIdHeader].FirstOrDefault();
            var deviceName = Request.Headers[DeviceService.DeviceNameHeader].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(hardwareId))
                return BadRequest($"Missing {DeviceService.HardwareIdHeader} header.");

            var device = await _deviceService.ResolveByHardwareIdAsync(userId, hardwareId, deviceName);
            await _inputEventService.SaveAsync(device.Id, request);
            return Ok();
        }

        [HttpGet("counts")]
        [EndpointName("getInputCounts")]
        public async Task<ActionResult<InputCountsResponse>> GetCounts(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var userId = _currentUser.GetUserId();
            return await _inputEventService.GetCountsAsync(userId, deviceId, start, end);
        }
    }
}
