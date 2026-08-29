namespace Heartbeat.Core;

public readonly record struct DateRange(DateTime UtcStart, DateTime UtcEnd)
{
    public static DateRange Day(DateTimeOffset date)
    {
        var dayStart = new DateTimeOffset(date.Date, date.Offset).UtcDateTime;
        return new DateRange(dayStart, dayStart.AddDays(1));
    }
}
