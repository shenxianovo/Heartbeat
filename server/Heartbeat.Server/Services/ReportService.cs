using Heartbeat.Core;
using Heartbeat.Core.DTOs.Reports;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class ReportService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        public async Task<DailyReportResponse> GetDailyReportAsync(string ownerId, long? deviceId, DateTimeOffset date)
        {
            var range = DateRange.Day(date);
            var apps = await AggregateAsync(ownerId, deviceId, range);

            return new DailyReportResponse
            {
                Date = date.Date.ToString("yyyy-MM-dd"),
                TotalSeconds = apps.Sum(a => a.DurationSeconds),
                Apps = apps
            };
        }

        public async Task<WeeklyReportResponse> GetWeeklyReportAsync(string ownerId, long? deviceId, DateTimeOffset date)
        {
            var range = DateRange.Week(date);
            var apps = await AggregateAsync(ownerId, deviceId, range);

            var d = date.Date;
            var dayOfWeek = d.DayOfWeek;
            var mondayOffset = dayOfWeek == DayOfWeek.Sunday ? -6 : -(int)dayOfWeek + 1;
            var monday = d.AddDays(mondayOffset);

            return new WeeklyReportResponse
            {
                WeekStart = monday.ToString("yyyy-MM-dd"),
                WeekEnd = monday.AddDays(6).ToString("yyyy-MM-dd"),
                TotalSeconds = apps.Sum(a => a.DurationSeconds),
                Apps = apps
            };
        }

        private async Task<List<AppDurationItem>> AggregateAsync(string ownerId, long? deviceId, DateRange range)
        {
            var query = _db.AppUsages
                .Where(x => x.Device.OwnerId == ownerId)
                .Where(x => x.StartTime >= range.UtcStart && x.StartTime < range.UtcEnd);

            if (deviceId.HasValue)
                query = query.Where(x => x.DeviceId == deviceId.Value);

            return await query
                .GroupBy(x => x.AppId)
                .Select(g => new AppDurationItem
                {
                    AppId = g.Key,
                    DurationSeconds = g.Sum(x => x.DurationSeconds)
                })
                .OrderByDescending(x => x.DurationSeconds)
                .ToListAsync();
        }
    }
}
