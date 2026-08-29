using Heartbeat.Core.DTOs.Reports;
using Heartbeat.Server.Calendar;
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
        [ProducesResponseType<DailyReportResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<CalendarWindowError>(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<DailyReportResponse>> GetDailyReport(
            [FromQuery] long? deviceId,
            [FromQuery] LocalCalendarWindowEnvelope window)
        {
            var userId = _currentUser.GetUserId();
            var result = await _reportService.GetDailyReportAsync(userId, deviceId, window);
            return result.Error == null ? result.Report! : BadRequest(result.Error);
        }

        [HttpGet("weekly")]
        [EndpointName("getWeeklyReport")]
        public async Task<ActionResult<WeeklyReportResponse>> GetWeeklyReport(
            [FromQuery] long? deviceId,
            [FromQuery] DateTimeOffset? date)
        {
            var userId = _currentUser.GetUserId();
            var targetDate = date ?? DateTimeOffset.UtcNow;
            return await _reportService.GetWeeklyReportAsync(userId, deviceId, targetDate);
        }
    }
}
