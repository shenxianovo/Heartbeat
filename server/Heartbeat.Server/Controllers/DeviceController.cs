using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/devices")]
    public class DeviceController(DeviceService deviceService) : ControllerBase
    {
        private readonly DeviceService _deviceService = deviceService;

        [HttpGet]
        public async Task<List<DeviceInfoResponse>> GetDevices()
        {
            return await _deviceService.GetAllAsync();
        }

        [HttpGet("{deviceId:long}")]
        [ProducesResponseType(typeof(DeviceStatusResponse), 200)]
        public async Task<IActionResult> GetDevice([FromRoute] long deviceId)
        {
            var device = await _deviceService.GetStatusAsync(deviceId);
            if (device == null) return NotFound();
            return Ok(device);
        }

        [Authorize]
        [HttpPost("heartbeat")]
        public async Task<IActionResult> Upload([FromBody] DeviceStatusRequest status)
        {
            var rawHeader = Request.Headers[DeviceService.DeviceNameHeader].FirstOrDefault();
            var device = await _deviceService.ResolveByNameAsync(rawHeader);
            if (device == null)
                return BadRequest($"Missing {DeviceService.DeviceNameHeader} header.");

            await _deviceService.UpdateStatusAsync(device, status.CurrentApp);
            return NoContent();
        }
    }
}
