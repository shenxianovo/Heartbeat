using Heartbeat.Core;

namespace Heartbeat.Core.Tests;

public class DateRangeTests
{
    [Fact]
    public void Day_UtcOffset_ReturnsCorrectBoundaries()
    {
        var date = new DateTimeOffset(2025, 6, 15, 14, 30, 0, TimeSpan.Zero);

        var range = DateRange.Day(date);

        Assert.Equal(new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc), range.UtcStart);
        Assert.Equal(new DateTime(2025, 6, 16, 0, 0, 0, DateTimeKind.Utc), range.UtcEnd);
    }

    [Fact]
    public void Day_PositiveOffset_ShiftsToUtc()
    {
        // UTC+8, local date is June 15, but UTC start is June 14 16:00
        var date = new DateTimeOffset(2025, 6, 15, 10, 0, 0, TimeSpan.FromHours(8));

        var range = DateRange.Day(date);

        Assert.Equal(new DateTime(2025, 6, 14, 16, 0, 0, DateTimeKind.Utc), range.UtcStart);
        Assert.Equal(new DateTime(2025, 6, 15, 16, 0, 0, DateTimeKind.Utc), range.UtcEnd);
    }

    [Fact]
    public void Day_NegativeOffset_ShiftsToUtc()
    {
        // UTC-5, local date is June 15, UTC start is June 15 05:00
        var date = new DateTimeOffset(2025, 6, 15, 10, 0, 0, TimeSpan.FromHours(-5));

        var range = DateRange.Day(date);

        Assert.Equal(new DateTime(2025, 6, 15, 5, 0, 0, DateTimeKind.Utc), range.UtcStart);
        Assert.Equal(new DateTime(2025, 6, 16, 5, 0, 0, DateTimeKind.Utc), range.UtcEnd);
    }
}
