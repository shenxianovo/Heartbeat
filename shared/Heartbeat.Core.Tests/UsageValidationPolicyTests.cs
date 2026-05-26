using Heartbeat.Core;
using Heartbeat.Core.DTOs.Usage;

namespace Heartbeat.Core.Tests;

public class UsageValidationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static AppUsageItem Item(string app, DateTimeOffset start, DateTimeOffset end) => new()
    {
        AppName = app,
        StartTime = start,
        EndTime = end
    };

    [Fact]
    public void ValidRecord_Passes()
    {
        var usages = new List<AppUsageItem>
        {
            Item("VSCode", Now.AddMinutes(-10), Now.AddMinutes(-5))
        };

        var result = UsageValidationPolicy.Filter(usages, Now);

        Assert.Single(result);
    }

    [Fact]
    public void EmptyAppName_Rejected()
    {
        var usages = new List<AppUsageItem>
        {
            Item("", Now.AddMinutes(-10), Now.AddMinutes(-5))
        };

        var result = UsageValidationPolicy.Filter(usages, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void EndBeforeStart_Rejected()
    {
        var usages = new List<AppUsageItem>
        {
            Item("App", Now.AddMinutes(-2), Now.AddMinutes(-5))
        };

        var result = UsageValidationPolicy.Filter(usages, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void YearBefore2020_Rejected()
    {
        var usages = new List<AppUsageItem>
        {
            Item("App", new DateTimeOffset(2019, 12, 31, 23, 0, 0, TimeSpan.Zero), Now)
        };

        var result = UsageValidationPolicy.Filter(usages, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void FutureBeyondSkewTolerance_Rejected()
    {
        var usages = new List<AppUsageItem>
        {
            Item("App", Now.AddMinutes(5), Now.AddMinutes(15))
        };

        var result = UsageValidationPolicy.Filter(usages, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void FutureWithinSkewTolerance_Passes()
    {
        var usages = new List<AppUsageItem>
        {
            Item("App", Now.AddMinutes(-5), Now.AddMinutes(9))
        };

        var result = UsageValidationPolicy.Filter(usages, Now);

        Assert.Single(result);
    }

    [Fact]
    public void DurationExceeds24Hours_Rejected()
    {
        var usages = new List<AppUsageItem>
        {
            Item("App", Now.AddHours(-25), Now.AddMinutes(-5))
        };

        var result = UsageValidationPolicy.Filter(usages, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void DurationExactly24Hours_Passes()
    {
        var start = Now.AddHours(-24);
        var usages = new List<AppUsageItem>
        {
            Item("App", start, start.AddHours(24))
        };

        var result = UsageValidationPolicy.Filter(usages, Now);

        Assert.Single(result);
    }

    [Fact]
    public void DefaultStartTime_Rejected()
    {
        var usages = new List<AppUsageItem>
        {
            Item("App", default, Now)
        };

        var result = UsageValidationPolicy.Filter(usages, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void ResultIsSortedByStartTime()
    {
        var usages = new List<AppUsageItem>
        {
            Item("B", Now.AddMinutes(-3), Now.AddMinutes(-1)),
            Item("A", Now.AddMinutes(-10), Now.AddMinutes(-8))
        };

        var result = UsageValidationPolicy.Filter(usages, Now);

        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].AppName);
        Assert.Equal("B", result[1].AppName);
    }
}
