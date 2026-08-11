using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Server.Services;
using Heartbeat.Server.Filters;
using Heartbeat.Core;
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
        public async Task<ActionResult<List<DeviceInfoResponse>>> GetDevices()
        {
            var userId = _currentUser.GetUserId();
            return await _deviceService.GetAllAsync(userId);
        }

        [HttpGet("{deviceId:long}")]
        [EndpointName("getDevice")]
        public async Task<ActionResult<DeviceStatusResponse>> GetDevice([FromRoute] long deviceId)
        {
            var userId = _currentUser.GetUserId();
            var device = await _deviceService.GetStatusAsync(deviceId, userId);
            if (device == null) return NotFound();
            return device;
        }

        [HttpPost("heartbeat")]
        [EndpointName("uploadHeartbeat")]
        [RequireHeartbeatProtocol]
        public async Task<IActionResult> Upload([FromBody] DeviceStatusRequest status)
        {
            if (status.CurrentApp is not null)
                return UpgradeRequiredResult.Create(Response, "Legacy CurrentApp presence is no longer accepted. Update Heartbeat.");
            if (!string.IsNullOrWhiteSpace(status.CurrentAppDisplayName)
                && string.IsNullOrWhiteSpace(status.CurrentAppIdentityKey))
                return BadRequest("CurrentAppIdentityKey is required when CurrentAppDisplayName is present.");
            if (!string.IsNullOrWhiteSpace(status.CurrentAppIdentityKey))
            {
                try { _ = AppIdentityKeys.Normalize(status.CurrentAppIdentityKey); }
                catch (ArgumentException ex) { return BadRequest(ex.Message); }
            }

            var userId = _currentUser.GetUserId();
            var hardwareId = Request.Headers[DeviceService.HardwareIdHeader].FirstOrDefault();
            var deviceName = Request.Headers[DeviceService.DeviceNameHeader].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(hardwareId))
                return BadRequest($"Missing {DeviceService.HardwareIdHeader} header.");

            var device = await _deviceService.ResolveByHardwareIdAsync(userId, hardwareId, deviceName);
            try
            {
                await _deviceService.UpdateStatusAsync(
                    device, status.CurrentAppIdentityKey, status.CurrentAppDisplayName);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            return NoContent();
        }
    }
}
