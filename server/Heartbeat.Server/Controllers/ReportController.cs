using Heartbeat.Core.DTOs.Reports;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/reports")]
    [Authorize]
    public class ReportController(ReportService reportService, ICurrentUserService currentUser) : ControllerBase
    {
        private readonly ReportService _reportService = reportService;
        private readonly ICurrentUserService _currentUser = currentUser;

        [HttpGet("daily")]
        [EndpointName("getDailyReport")]
        [ProducesResponseType(typeof(DailyReportResponse), 200)]
        public async Task<IActionResult> GetDailyReport(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? date)
        {
            var userId = _currentUser.GetUserId();
            var targetDate = date ?? DateTimeOffset.UtcNow;
            var report = await _reportService.GetDailyReportAsync(userId, deviceId, targetDate);
            return Ok(report);
        }

        [HttpGet("weekly")]
        [EndpointName("getWeeklyReport")]
        [ProducesResponseType(typeof(WeeklyReportResponse), 200)]
        public async Task<IActionResult> GetWeeklyReport(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? date)
        {
            var userId = _currentUser.GetUserId();
            var targetDate = date ?? DateTimeOffset.UtcNow;
            var report = await _reportService.GetWeeklyReportAsync(userId, deviceId, targetDate);
            return Ok(report);
        }
    }
}
