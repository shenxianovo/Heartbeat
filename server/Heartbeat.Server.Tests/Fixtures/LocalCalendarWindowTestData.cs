using Heartbeat.Server.Calendar;

namespace Heartbeat.Server.Tests.Fixtures;

internal static class LocalCalendarWindowTestData
{
    public static LocalCalendarWindowEnvelope UtcDay(DateTimeOffset date)
    {
        var start = new DateTimeOffset(date.UtcDateTime.Date, TimeSpan.Zero);
        return new LocalCalendarWindowEnvelope
        {
            Version = 1,
            Kind = "day",
            LocalDate = start.ToString("yyyy-MM-dd"),
            TimeZone = "UTC",
            Start = start,
            EndExclusive = start.AddDays(1),
        };
    }
}
