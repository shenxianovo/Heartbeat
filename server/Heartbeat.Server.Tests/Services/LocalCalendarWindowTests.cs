using System.Text.Json;
using Heartbeat.Server.Calendar;

namespace Heartbeat.Server.Tests.Services;

public sealed class LocalCalendarWindowTests
{
    public sealed record GoldenScenario(
        string Name,
        string LocalDate,
        string TimeZone,
        string? Start,
        string? EndExclusive,
        int? DurationHours,
        string? WeekStart,
        string? WeekEndExclusive,
        int? WeekDurationHours,
        string? Error);

    public static TheoryData<GoldenScenario> GoldenScenarios()
    {
        var json = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "calendar-window-golden-scenarios.json"));
        var scenarios = JsonSerializer.Deserialize<List<GoldenScenario>>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var data = new TheoryData<GoldenScenario>();
        foreach (var scenario in scenarios) data.Add(scenario);
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenScenarios))]
    public void Resolve_UsesSharedBrowserGoldenScenarios(GoldenScenario scenario)
    {
        var envelope = new LocalCalendarWindowEnvelope
        {
            Version = 1,
            Kind = "day",
            LocalDate = scenario.LocalDate,
            TimeZone = scenario.TimeZone,
            Start = scenario.Start == null ? DateTimeOffset.UnixEpoch : DateTimeOffset.Parse(scenario.Start),
            EndExclusive = scenario.EndExclusive == null ? DateTimeOffset.UnixEpoch : DateTimeOffset.Parse(scenario.EndExclusive),
        };

        var result = LocalCalendarWindowValidator.Resolve(envelope);

        if (scenario.Error != null)
        {
            Assert.Null(result.Window);
            Assert.Equal(scenario.Error, result.Error!.Code);
            return;
        }

        Assert.Null(result.Error);
        Assert.Equal(scenario.LocalDate, result.Window!.LocalDate);
        Assert.Equal(scenario.TimeZone, result.Window.TimeZone);
        Assert.Equal(DateTimeOffset.Parse(scenario.Start!), result.Window.Start);
        Assert.Equal(DateTimeOffset.Parse(scenario.EndExclusive!), result.Window.EndExclusive);
        Assert.Equal(
            (double)scenario.DurationHours!.Value,
            (result.Window.EndExclusive - result.Window.Start).TotalHours);

        var weekResult = LocalCalendarWindowValidator.Resolve(envelope with
        {
            Kind = "week",
            Start = DateTimeOffset.Parse(scenario.WeekStart!),
            EndExclusive = DateTimeOffset.Parse(scenario.WeekEndExclusive!),
        });

        Assert.Null(weekResult.Error);
        Assert.Equal("week", weekResult.Window!.Kind);
        Assert.Equal(
            (double)scenario.WeekDurationHours!.Value,
            (weekResult.Window.EndExclusive - weekResult.Window.Start).TotalHours);
    }

    [Theory]
    [InlineData("2026-8-29")]
    [InlineData("2026-02-29")]
    [InlineData("0000-01-01")]
    [InlineData("not-a-date")]
    public void Resolve_RejectsInvalidLocalDate(string localDate)
    {
        var result = LocalCalendarWindowValidator.Resolve(ValidEnvelope() with { LocalDate = localDate });

        Assert.Equal("invalid_local_date", result.Error!.Code);
    }

    [Fact]
    public void Resolve_RejectsUnsupportedTimezone()
    {
        var result = LocalCalendarWindowValidator.Resolve(ValidEnvelope() with { TimeZone = "Mars/Olympus_Mons" });

        Assert.Equal("unsupported_timezone", result.Error!.Code);
    }

    [Fact]
    public void Resolve_RejectsOneInstantDifferenceAsCalendarRulesMismatch()
    {
        var envelope = ValidEnvelope();
        var result = LocalCalendarWindowValidator.Resolve(envelope with
        {
            EndExclusive = envelope.EndExclusive.AddSeconds(1),
        });

        Assert.Equal("calendar_rules_mismatch", result.Error!.Code);
        Assert.Contains("TZDB", result.Error.Message);
    }

    [Fact]
    public void Resolve_RecomputesTheMondayWeekAcrossSpringForward()
    {
        var result = LocalCalendarWindowValidator.Resolve(ValidEnvelope() with
        {
            Kind = "week",
            LocalDate = "2026-03-08",
            TimeZone = "America/New_York",
            Start = DateTimeOffset.Parse("2026-03-02T05:00:00Z"),
            EndExclusive = DateTimeOffset.Parse("2026-03-09T04:00:00Z"),
        });

        Assert.Null(result.Error);
        Assert.Equal("week", result.Window!.Kind);
        Assert.Equal(167, (result.Window.EndExclusive - result.Window.Start).TotalHours);
    }

    [Fact]
    public void ResolvedWindow_WindowKeyContainsTheCanonicalCalendarIdentity()
    {
        var result = LocalCalendarWindowValidator.Resolve(ValidEnvelope());

        Assert.Null(result.Error);
        Assert.Equal(
            "local-calendar-window|1|day|2026-08-29|Asia/Shanghai|2026-08-28T16:00:00.0000000Z|2026-08-29T16:00:00.0000000Z",
            result.Window!.WindowKey.Value);

        var window = result.Window;
        Assert.NotEqual(window.WindowKey, (window with { Version = 2 }).WindowKey);
        Assert.NotEqual(window.WindowKey, (window with { Kind = "week" }).WindowKey);
        Assert.NotEqual(window.WindowKey, (window with { TimeZone = "Etc/UTC" }).WindowKey);
        Assert.NotEqual(window.WindowKey, (window with { EndExclusive = window.EndExclusive.AddHours(1) }).WindowKey);
    }

    private static LocalCalendarWindowEnvelope ValidEnvelope() => new()
    {
        Version = 1,
        Kind = "day",
        LocalDate = "2026-08-29",
        TimeZone = "Asia/Shanghai",
        Start = DateTimeOffset.Parse("2026-08-28T16:00:00Z"),
        EndExclusive = DateTimeOffset.Parse("2026-08-29T16:00:00Z"),
    };
}
