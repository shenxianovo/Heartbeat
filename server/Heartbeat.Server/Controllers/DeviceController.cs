using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Heartbeat.Server.Data;
using Heartbeat.Server.Services;
using Heartbeat.Core.DTOs;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/devices")]
    public class DeviceController(AppDbContext db, UsageService usageService) : ControllerBase
    {
        private readonly AppDbContext _db = db;
        private readonly UsageService _usageService = usageService;

        [HttpGet]
        public async Task<List<DeviceInfoResponse>> GetDevices()
        {
            return await _db.Devices
                .Select(x => new DeviceInfoResponse
                {
                    Id = x.Id,
                    Name = x.DeviceName
                })
                .ToListAsync();
        }

        [HttpGet("{deviceId:long}/status")]
        public async Task<IActionResult> GetStatus([FromRoute] long deviceId)
        {
            var device = await _db.Devices
                .Where(d => d.Id == deviceId)
                .Select(d => new DeviceStatusResponse
                {
                    CurrentApp = d.CurrentApp,
                    LastSeen = d.LastSeen
                })
                .FirstOrDefaultAsync();

            if (device == null) return NotFound();

            return Ok(device);
        }

        [HttpGet("{deviceId:long}/usage")]
        public async Task<IActionResult> GetUsage([FromRoute] long deviceId, [FromQuery] DateTimeOffset? date)
        {
            var result = await _usageService.GetUsageAsync(deviceId, date);
            return Ok(result);
        }
    }
}
