using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Reports;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Core.DTOs.Usage;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    // 查询端点统一用 ActionResult<T>:响应 schema 由返回类型自动推断进 OpenAPI,
    // NSwag 客户端才能生成带类型的方法(IActionResult 是不透明盒子,会生成 Promise<void>)。
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
        public async Task<ActionResult<List<DeviceInfoResponse>>> GetDevices(string username)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            return await deviceService.GetAllAsync(user.Id);
        }

        [HttpGet("reports/daily")]
        [EndpointName("getUserDailyReport")]
        public async Task<ActionResult<DailyReportResponse>> GetDailyReport(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? date)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var targetDate = date ?? DateTimeOffset.UtcNow;
            return await reportService.GetDailyReportAsync(user.Id, deviceId, targetDate);
        }

        [HttpGet("reports/weekly")]
        [EndpointName("getUserWeeklyReport")]
        public async Task<ActionResult<WeeklyReportResponse>> GetWeeklyReport(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? date)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var targetDate = date ?? DateTimeOffset.UtcNow;
            return await reportService.GetWeeklyReportAsync(user.Id, deviceId, targetDate);
        }

        [HttpGet("usage")]
        [EndpointName("getUserUsage")]
        public async Task<ActionResult<List<AppUsageResponse>>> GetUsage(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            return await usageService.GetUsageAsync(user.Id, deviceId, start, end);
        }

        [HttpGet("segments")]
        [EndpointName("getUserSegments")]
        public async Task<ActionResult<List<SegmentResponse>>> GetSegments(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] string? source,
            [FromQuery] long? appId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            return await usageService.GetSegmentsAsync(user.Id, deviceId, source, appId, start, end);
        }

        [HttpGet("apps")]
        [EndpointName("getUserApps")]
        public async Task<ActionResult<List<AppInfoResponse>>> GetApps(string username)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            return await appService.GetAppsForUserAsync(user.Id);
        }

        [HttpGet("devices/{deviceId:long}/status")]
        [EndpointName("getUserDeviceStatus")]
        public async Task<ActionResult<DeviceStatusResponse>> GetDeviceStatus(string username, long deviceId)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            var status = await deviceService.GetStatusAsync(deviceId, user.Id);
            if (status == null) return NotFound();
            return status;
        }

        [HttpGet("input-events/key-frequency")]
        [EndpointName("getUserKeyFrequency")]
        public async Task<ActionResult<KeyFrequencyResponse>> GetKeyFrequency(
            string username,
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? start,
            [FromQuery] DateTimeOffset? end)
        {
            var user = await userService.ResolveByUsernameAsync(username);
            if (user == null) return NotFound();

            return await inputEventService.GetKeyFrequencyAsync(user.Id, deviceId, start, end);
        }
    }
}
