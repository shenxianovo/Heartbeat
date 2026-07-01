using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/devices")]
    [Authorize]
    public class DeviceController(DeviceService deviceService, ICurrentUserService currentUser) : ControllerBase
    {
        private readonly DeviceService _deviceService = deviceService;
        private readonly ICurrentUserService _currentUser = currentUser;

        [HttpGet]
        [EndpointName("getDevices")]
        public async Task<List<DeviceInfoResponse>> GetDevices()
        {
            var userId = _currentUser.GetUserId();
            return await _deviceService.GetAllAsync(userId);
        }

        [HttpGet("{deviceId:long}")]
        [EndpointName("getDevice")]
        [ProducesResponseType(typeof(DeviceStatusResponse), 200)]
        public async Task<IActionResult> GetDevice([FromRoute] long deviceId)
        {
            var userId = _currentUser.GetUserId();
            var device = await _deviceService.GetStatusAsync(deviceId, userId);
            if (device == null) return NotFound();
            return Ok(device);
        }

        [HttpPost("heartbeat")]
        [EndpointName("uploadHeartbeat")]
        public async Task<IActionResult> Upload([FromBody] DeviceStatusRequest status)
        {
            var userId = _currentUser.GetUserId();
            var hardwareId = Request.Headers[DeviceService.HardwareIdHeader].FirstOrDefault();
            var deviceName = Request.Headers[DeviceService.DeviceNameHeader].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(hardwareId))
                return BadRequest($"Missing {DeviceService.HardwareIdHeader} header.");

            var device = await _deviceService.ResolveByHardwareIdAsync(userId, hardwareId, deviceName);
            await _deviceService.UpdateStatusAsync(device, status.CurrentApp);
            return NoContent();
        }
    }
}
