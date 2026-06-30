using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/users/{username}")]
    public class PublicUserController(
        UserService userService,
        DeviceService deviceService,
        ReportService reportService,
        UsageService usageService,
        AppService appService,
        InputEventService inputEventService) : ControllerBase
    {
        [HttpGet("devices")]
        public async Task<IActionResult> GetDevices(string username)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var devices = await deviceService.GetAllAsync(user.Id);
            return Ok(devices);
        }

        [HttpGet("reports/daily")]
        public async Task<IActionResult> GetDailyReport(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? date)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var targetDate = date ?? DateTimeOffset.UtcNow;
            var report = await reportService.GetDailyReportAsync(user.Id, deviceId, targetDate);
            return Ok(report);
        }

        [HttpGet("reports/weekly")]
        public async Task<IActionResult> GetWeeklyReport(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? date)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var targetDate = date ?? DateTimeOffset.UtcNow;
            var report = await reportService.GetWeeklyReportAsync(user.Id, deviceId, targetDate);
            return Ok(report);
        }

        [HttpGet("usage")]
        public async Task<IActionResult> GetUsage(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var result = await usageService.GetUsageAsync(user.Id, deviceId, start, end);
            return Ok(result);
        }

        [HttpGet("apps")]
        public async Task<IActionResult> GetApps(string username)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var apps = await appService.GetAppsForUserAsync(user.Id);
            return Ok(apps);
        }

        [HttpGet("devices/{deviceId:long}/status")]
        public async Task<IActionResult> GetDeviceStatus(string username, long deviceId)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var status = await deviceService.GetStatusAsync(deviceId, user.Id);
            if (status == null) return NotFound();
            return Ok(status);
        }

        [HttpGet("input-events/key-frequency")]
        public async Task<IActionResult> GetKeyFrequency(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var result = await inputEventService.GetKeyFrequencyAsync(user.Id, deviceId, start, end);
            return Ok(result);
        }
    }
}
