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

public sealed record ResolvedCalendarWindow(
    int Version,
    string Kind,
    string LocalDate,
    string TimeZone,
    DateTimeOffset Start,
    DateTimeOffset EndExclusive,
    LocalDate CivilStartDate,
    LocalDate CivilEndExclusiveDate);

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
}
