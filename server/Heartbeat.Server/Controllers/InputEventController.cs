using Heartbeat.Core.DTOs.Input;
using Heartbeat.Server.Services;
using Heartbeat.Server.Filters;
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
        [RequireHeartbeatProtocol]
        public async Task<IActionResult> Upload([FromBody] InputEventUploadRequest request)
        {
            if (request.Events == null || request.Events.Count == 0)
                return BadRequest("Events cannot be empty.");

            try
            {
                InputEventIngestContract.Validate(request.Events);
            }
            catch (InputEventIngestContractException ex)
            {
                return UnprocessableEntity(ex.Message);
            }

            var userId = _currentUser.GetUserId();
            var hardwareId = Request.Headers[DeviceService.HardwareIdHeader].FirstOrDefault();
            var deviceName = Request.Headers[DeviceService.DeviceNameHeader].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(hardwareId))
                return BadRequest($"Missing {DeviceService.HardwareIdHeader} header.");

            try
            {
                await _inputEventService.IngestAsync(userId, hardwareId, deviceName, request);
            }
            catch (FactIngestException ex)
            {
                return ex.IsConflict ? Conflict(ex.Message) : UnprocessableEntity(ex.Message);
            }
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
