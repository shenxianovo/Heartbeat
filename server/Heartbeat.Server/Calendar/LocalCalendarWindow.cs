using NodaTime;
using NodaTime.Text;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Heartbeat.Server.Calendar;

public sealed record LocalCalendarWindowEnvelope
{
    [BindRequired]
    public int Version { get; init; }

    [BindRequired, Required]
    public string? Kind { get; init; }

    [BindRequired, Required]
    public string? LocalDate { get; init; }

    [BindRequired, Required]
    public string? TimeZone { get; init; }

    [BindRequired]
    public DateTimeOffset Start { get; init; }

    [BindRequired]
    public DateTimeOffset EndExclusive { get; init; }
}

/// <summary>
/// Analytics 在严格验证日历窗口后才产生的持久化身份。构造器不对程序集外暴露，
/// 避免缓存与生成锁边界退化成接受任意调用方字符串。
/// </summary>
public sealed record CalendarWindowKey
{
    internal CalendarWindowKey(string value) => Value = value;

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ResolvedCalendarWindow(
    int Version,
    string Kind,
    string LocalDate,
    string TimeZone,
    DateTimeOffset Start,
    DateTimeOffset EndExclusive,
    LocalDate CivilStartDate,
    LocalDate CivilEndExclusiveDate)
{
    public DateOnly CivilStartDateOnly =>
        new(CivilStartDate.Year, CivilStartDate.Month, CivilStartDate.Day);

    /// <summary>
    /// Analytics-owned persistent identity derived only after strict calendar validation. The readable,
    /// versioned canonical form keeps every identity component diagnosable in stored rows and lock keys.
    /// </summary>
    public CalendarWindowKey WindowKey => new(
        $"local-calendar-window|{Version}|{Kind}|{LocalDate}|{TimeZone}|{Start.UtcDateTime:O}|{EndExclusive.UtcDateTime:O}");
}

public sealed record CalendarWindowError(string Code, string Message);

public sealed record CalendarWindowValidationResult(
    ResolvedCalendarWindow? Window,
    CalendarWindowError? Error)
{
    public static CalendarWindowValidationResult Success(ResolvedCalendarWindow window) => new(window, null);
    public static CalendarWindowValidationResult Failure(string code, string message) =>
        new(null, new CalendarWindowError(code, message));
}

public static class LocalCalendarWindowValidator
{
    private const int CurrentVersion = 1;
    private static readonly LocalDatePattern DatePattern =
        LocalDatePattern.CreateWithInvariantCulture("uuuu-MM-dd");

    public static CalendarWindowValidationResult Resolve(LocalCalendarWindowEnvelope envelope)
    {
        if (envelope.Version != CurrentVersion || envelope.Kind is not ("day" or "week"))
        {
            return CalendarWindowValidationResult.Failure(
                "unsupported_calendar_window",
                $"Only version {CurrentVersion} day or week calendar windows are supported.");
        }

        var dateResult = DatePattern.Parse(envelope.LocalDate ?? string.Empty);
        if (!dateResult.Success || dateResult.Value.ToString("yyyy-MM-dd", null) != envelope.LocalDate)
        {
            return CalendarWindowValidationResult.Failure(
                "invalid_local_date",
                $"LocalDate must be a real Gregorian date in yyyy-MM-dd form: {envelope.LocalDate}");
        }

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(envelope.TimeZone ?? string.Empty);
        if (zone == null)
        {
            return CalendarWindowValidationResult.Failure(
                "unsupported_timezone",
                $"Timezone is not present in Analytics TZDB {DateTimeZoneProviders.Tzdb.VersionId}: {envelope.TimeZone}");
        }

        Instant expectedStart;
        Instant expectedEnd;
        LocalDate windowStartDate;
        var isWeek = envelope.Kind == "week";
        var windowLengthDays = isWeek ? 7 : 1;
        try
        {
            var selectedDateStart = zone.AtStartOfDay(dateResult.Value).ToInstant();
            windowStartDate = isWeek
                ? dateResult.Value.PlusDays(1 - (int)dateResult.Value.DayOfWeek)
                : dateResult.Value;
            expectedStart = isWeek
                ? zone.AtStartOfDay(windowStartDate).ToInstant()
                : selectedDateStart;
            expectedEnd = zone.AtStartOfDay(windowStartDate.PlusDays(windowLengthDays)).ToInstant();
        }
        catch (SkippedTimeException)
        {
            return CalendarWindowValidationResult.Failure(
                "nonexistent_civil_date",
                $"Civil date {envelope.LocalDate} does not exist in {envelope.TimeZone} according to Analytics TZDB {DateTimeZoneProviders.Tzdb.VersionId}.");
        }

        var expectedStartValue = expectedStart.ToDateTimeOffset();
        var expectedEndValue = expectedEnd.ToDateTimeOffset();
        if (envelope.Start.ToUniversalTime() != expectedStartValue ||
            envelope.EndExclusive.ToUniversalTime() != expectedEndValue)
        {
            return CalendarWindowValidationResult.Failure(
                "calendar_rules_mismatch",
                $"Browser sent [{envelope.Start:O}, {envelope.EndExclusive:O}); Analytics TZDB {DateTimeZoneProviders.Tzdb.VersionId} resolved [{expectedStartValue:O}, {expectedEndValue:O}). Update the Browser or Analytics timezone data before retrying.");
        }

        return CalendarWindowValidationResult.Success(new ResolvedCalendarWindow(
            CurrentVersion,
            envelope.Kind,
            envelope.LocalDate,
            envelope.TimeZone!,
            expectedStartValue,
            expectedEndValue,
            windowStartDate,
            windowStartDate.PlusDays(windowLengthDays)));
    }

    public static CalendarWindowValidationResult ResolveDay(LocalCalendarWindowEnvelope envelope)
    {
        if (envelope.Kind != "day")
        {
            return CalendarWindowValidationResult.Failure(
                "unsupported_calendar_window",
                "This endpoint requires a version 1 day calendar window.");
        }

        return Resolve(envelope);
    }
}
