using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Observers.Sources.PhysicalCalendar;

/// <summary>Bounded RFC 5545 VEVENT import. Recurrences and TZID values are retained, never expanded or guessed.</summary>
public sealed class ICalendarSource : IRecordSource
{
    public string Name => "icalendar-ics";
    private static readonly object StringItem = new { type = "string", nullable = false };
    public static SourceSchema Schema { get; } = new(Guid.Parse("019a0000-0300-7000-8000-000000000001"),
        "source-calendar-events", "iCalendar VEVENT", SourceFields.WithIdentity(new
        {
            uid = SourceFields.String(), sequence = SourceFields.Integer(nullable: true),
            recurrenceId = SourceFields.String(nullable: true), recurrenceKind = SourceFields.String(nullable: true),
            recurrenceTzid = SourceFields.String(nullable: true), recurrenceRange = SourceFields.String(nullable: true),
            start = SourceFields.String(nullable: true), startKind = SourceFields.String(nullable: true), startTzid = SourceFields.String(nullable: true),
            end = SourceFields.String(nullable: true), endKind = SourceFields.String(nullable: true), endTzid = SourceFields.String(nullable: true),
            duration = SourceFields.String(nullable: true), summary = SourceFields.String(nullable: true),
            description = SourceFields.String(nullable: true), location = SourceFields.String(nullable: true), status = SourceFields.String(nullable: true),
            dtstamp = SourceFields.String(), recurrenceRules = SourceFields.Array(StringItem), exclusionDates = SourceFields.Array(StringItem),
            inclusionDates = SourceFields.Array(StringItem)
        }));

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CaptureKey);
        var bytes = await SourceJson.ReadBytesAsync(context.Input, cancellationToken);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("iCalendar must contain valid UTF-8.", error); }
        var lines = new List<string>();
        StringBuilder? unfolded = null;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            if (line[0] is ' ' or '\t')
            {
                if (unfolded is null) throw new InvalidDataException("Orphan folded line.");
                unfolded.Append(line.AsSpan(1));
            }
            else
            {
                if (unfolded is not null) lines.Add(unfolded.ToString());
                unfolded = new StringBuilder(line);
            }
            if (unfolded.Length > 262144) throw new InvalidDataException("Calendar content line exceeds limit.");
        }
        if (unfolded is not null) lines.Add(unfolded.ToString());
        var stack = new Stack<string>();
        var events = new List<List<Property>>();
        List<Property>? current = null;
        var header = new List<Property>();
        var closed = false;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var property = Parse(line);
            if (property.Name == "BEGIN")
            {
                var component = property.Value.ToUpperInvariant();
                if (closed || stack.Count == 0 && component != "VCALENDAR" || stack.Count > 16)
                    throw new InvalidDataException("Invalid calendar component structure.");
                if (component == "VEVENT")
                {
                    if (stack.Count != 1 || stack.Peek() != "VCALENDAR") throw new InvalidDataException("Invalid VEVENT nesting.");
                    current = []; events.Add(current);
                    if (events.Count > 10000) throw new InvalidDataException("Calendar event limit exceeded.");
                }
                stack.Push(component);
            }
            else if (property.Name == "END")
            {
                if (stack.Count == 0 || !string.Equals(stack.Pop(), property.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Mismatched calendar component.");
                if (property.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) current = null;
                if (stack.Count == 0) closed = true;
            }
            else if (stack.Count == 0) throw new InvalidDataException("Property outside calendar.");
            else if (stack.Peek() == "VEVENT") current!.Add(property);
            else if (stack.Count == 1) header.Add(property);
        }
        if (!closed || stack.Count != 0 || One(header, "VERSION", true)!.Value != "2.0" || One(header, "PRODID", true) is null)
            throw new InvalidDataException("Expected complete iCalendar 2.0 document.");
        var records = new List<SourceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var properties in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uid = Unescape(One(properties, "UID", true)!.Value);
            if (string.IsNullOrWhiteSpace(uid)) throw new InvalidDataException("Empty event UID.");
            var sequenceText = One(properties, "SEQUENCE")?.Value;
            int? sequence = null;
            if (sequenceText is not null)
            {
                if (!int.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw new InvalidDataException("Invalid SEQUENCE.");
                sequence = number;
            }
            var stamp = Temporal(One(properties, "DTSTAMP", true));
            if (stamp.Kind != "utc") throw new InvalidDataException("DTSTAMP must be UTC.");
            var start = Temporal(One(properties, "DTSTART"));
            var end = Temporal(One(properties, "DTEND"));
            var recurrenceProperty = One(properties, "RECURRENCE-ID");
            var recurrence = Temporal(recurrenceProperty);
            var range = recurrenceProperty?.Parameters.GetValueOrDefault("RANGE");
            if (range is not null && range != "THISANDFUTURE") throw new InvalidDataException("Unsupported recurrence range.");
            var duration = One(properties, "DURATION")?.Value;
            if (duration is not null && end.Value is not null) throw new InvalidDataException("DTEND and DURATION are mutually exclusive.");
            if (end.Value is not null && (start.Value is null || start.Kind != end.Kind || start.Tzid != end.Tzid)) throw new InvalidDataException("DTEND must match DTSTART value type and zone.");
            if (start.Value is not null && end.Value is not null && string.CompareOrdinal(start.Value, end.Value) >= 0)
                throw new InvalidDataException("DTEND must be later than DTSTART.");
            _ = One(properties, "RRULE");
            var native = Key(context.Scope, uid, recurrence.Kind, recurrence.Tzid, recurrence.Value);
            var mapped = SourceFields.Of(new
            {
                uid, sequence, recurrenceId = recurrence.Value, recurrenceKind = recurrence.Kind, recurrenceTzid = recurrence.Tzid, recurrenceRange = range,
                start = start.Value, startKind = start.Kind, startTzid = start.Tzid,
                end = end.Value, endKind = end.Kind, endTzid = end.Tzid, duration,
                summary = Text(properties, "SUMMARY"), description = Text(properties, "DESCRIPTION"), location = Text(properties, "LOCATION"),
                status = One(properties, "STATUS")?.Value, dtstamp = stamp.Value!,
                recurrenceRules = properties.Where(p => p.Name == "RRULE").Select(p => p.Line).ToArray(),
                exclusionDates = properties.Where(p => p.Name == "EXDATE").Select(p => p.Line).ToArray(),
                inclusionDates = properties.Where(p => p.Name == "RDATE").Select(p => p.Line).ToArray()
            });
            var evidence = Key(context.CaptureKey, native, sequence?.ToString(CultureInfo.InvariantCulture));
            if (!seen.Add(evidence)) throw new InvalidDataException("Ambiguous duplicate event identity and sequence in capture.");
            records.Add(new("icalendar-event", native, evidence, Schema, mapped, new Dictionary<string, SourceReference>(), start.Instant, end.Instant));
        }
        foreach (var record in records) { cancellationToken.ThrowIfCancellationRequested(); yield return record; }
    }

    private sealed record Property(string Name, Dictionary<string, string> Parameters, string Value, string Line);
    private sealed record CalendarTime(string? Value, string? Kind, string? Tzid, DateTimeOffset? Instant);
    private static CalendarTime Temporal(Property? property)
    {
        if (property is null) return new(null, null, null, null);
        var type = property.Parameters.GetValueOrDefault("VALUE", "DATE-TIME");
        var tzid = property.Parameters.GetValueOrDefault("TZID");
        var utc = property.Value.EndsWith('Z');
        if (type is not ("DATE" or "DATE-TIME") || tzid is not null && (type == "DATE" || utc)) throw new InvalidDataException("Invalid calendar time parameters.");
        var format = type == "DATE" ? "yyyyMMdd" : utc ? "yyyyMMdd'T'HHmmss'Z'" : "yyyyMMdd'T'HHmmss";
        if (!DateTime.TryParseExact(property.Value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new InvalidDataException("Invalid calendar date/time.");
        return new(property.Value, type == "DATE" ? "date" : utc ? "utc" : tzid is null ? "floating" : "tzid", tzid,
            utc ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : null);
    }
    private static Property? One(List<Property> properties, string name, bool required = false)
    {
        var matches = properties.Where(p => p.Name == name).ToArray();
        if (matches.Length > 1 || required && matches.Length == 0) throw new InvalidDataException($"Missing or duplicate {name}.");
        return matches.SingleOrDefault();
    }
    private static string? Text(List<Property> properties, string name) => One(properties, name) is { } p ? Unescape(p.Value) : null;
    private static string Unescape(string value)
    {
        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { result.Append(value[i]); continue; }
            if (++i == value.Length) throw new InvalidDataException("Incomplete iCalendar escape.");
            result.Append(value[i] switch { 'n' or 'N' => '\n', '\\' => '\\', ';' => ';', ',' => ',', _ => throw new InvalidDataException("Invalid iCalendar TEXT escape.") });
        }
        return result.ToString();
    }
    private static Property Parse(string line)
    {
        var parts = new List<string>(); var quoted = false; var from = 0; var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            if (quoted) continue;
            if (line[i] is ';' or ':') { parts.Add(line[from..i]); from = i + 1; }
            if (line[i] == ':') { colon = i; break; }
        }
        if (colon < 1 || quoted || parts[0].Length == 0) throw new InvalidDataException("Malformed iCalendar content line.");
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals < 1 || equals == part.Length - 1 || !parameters.TryAdd(part[..equals], part[(equals + 1)..].Trim('"')))
                throw new InvalidDataException("Malformed or duplicate calendar parameter.");
        }
        return new(parts[0].ToUpperInvariant(), parameters, line[(colon + 1)..], line);
    }
    internal static string Key(params string?[] parts) => SourceJson.Hash(JsonSerializer.Serialize(parts));
}
