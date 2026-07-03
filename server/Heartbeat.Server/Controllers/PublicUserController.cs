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
        [EndpointName("getUserDevices")]
        public async Task<IActionResult> GetDevices(string username)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var devices = await deviceService.GetAllAsync(user.Id);
            return Ok(devices);
        }

        [HttpGet("reports/daily")]
        [EndpointName("getUserDailyReport")]
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
        [EndpointName("getUserWeeklyReport")]
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
        [EndpointName("getUserUsage")]
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

        [HttpGet("segments")]
        [EndpointName("getUserSegments")]
        [ProducesResponseType(typeof(List<Heartbeat.Core.DTOs.Segments.SegmentResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSegments(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] string? source,
            [FromQuery] long? appId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var result = await usageService.GetSegmentsAsync(user.Id, deviceId, source, appId, start, end);
            return Ok(result);
        }

        [HttpGet("apps")]
        [EndpointName("getUserApps")]
        public async Task<IActionResult> GetApps(string username)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var apps = await appService.GetAppsForUserAsync(user.Id);
            return Ok(apps);
        }

        [HttpGet("devices/{deviceId:long}/status")]
        [EndpointName("getUserDeviceStatus")]
        public async Task<IActionResult> GetDeviceStatus(string username, long deviceId)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var status = await deviceService.GetStatusAsync(deviceId, user.Id);
            if (status == null) return NotFound();
            return Ok(status);
        }

        [HttpGet("input-events/key-frequency")]
        [EndpointName("getUserKeyFrequency")]
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
