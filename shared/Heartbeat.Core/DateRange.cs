namespace Heartbeat.Core;

public readonly record struct DateRange(DateTime UtcStart, DateTime UtcEnd)
{
    public static DateRange Day(DateTimeOffset date)
    {
        var dayStart = new DateTimeOffset(date.Date, date.Offset).UtcDateTime;
        return new DateRange(dayStart, dayStart.AddDays(1));
    }

    public static DateRange Week(DateTimeOffset date)
    {
        var d = date.Date;
        var dayOfWeek = d.DayOfWeek;
        var mondayOffset = dayOfWeek == DayOfWeek.Sunday ? -6 : -(int)dayOfWeek + 1;
        var monday = d.AddDays(mondayOffset);

        var weekStart = new DateTimeOffset(monday, date.Offset).UtcDateTime;
        var weekEnd = new DateTimeOffset(monday.AddDays(7), date.Offset).UtcDateTime;
        return new DateRange(weekStart, weekEnd);
    }
}
