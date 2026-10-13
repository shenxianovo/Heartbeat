using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Heartbeat.Observers.Sources.Telemetry;

/// <summary>Native OTLP JSON export requests: traces, logs, and gauge/sum metric points.</summary>
public sealed class OtlpJsonSource : IRecordSource
{
    private static readonly string[] SignalNames = ["resourceSpans", "resourceLogs", "resourceMetrics"];
    private static readonly string[] MetricTypes = ["gauge", "sum", "histogram", "exponentialHistogram", "summary"];
    public string Name => "otlp-json";
    private static SourceSchema Schema(int n, string resource, object fields) => new(
        Guid.Parse($"019a0000-0101-7000-8000-{n:000000000000}"), resource, resource, SourceFields.WithIdentity(fields));
    public static SourceSchema SpanSchema { get; } = Schema(1, "source-otlp-spans", new
    {
        traceId = SourceFields.String(), spanId = SourceFields.String(), name = SourceFields.String(),
        spanKind = SourceFields.Integer(true), statusCode = SourceFields.Integer(true), statusMessage = SourceFields.String(true),
        traceState = SourceFields.String(true), flags = SourceFields.Integer(true),
        droppedAttributesCount = SourceFields.Integer(true), droppedEventsCount = SourceFields.Integer(true), droppedLinksCount = SourceFields.Integer(true),
        startTimeUnixNano = SourceFields.String(true), endTimeUnixNano = SourceFields.String(true),
        resourceJson = SourceFields.String(true), scopeJson = SourceFields.String(true),
        exportAttributes = TelemetryJson.AttributeField, resourceAttributes = TelemetryJson.AttributeField, scopeAttributes = TelemetryJson.AttributeField,
        attributes = TelemetryJson.AttributeField, parentSpanId = SourceFields.Reference(required: false)
    });
    public static SourceSchema EventSchema { get; } = Schema(2, "source-otlp-span-events", new
    {
        name = SourceFields.String(), ordinal = SourceFields.Integer(), timeUnixNano = SourceFields.String(true),
        attributes = TelemetryJson.AttributeField, spanId = SourceFields.Reference()
    });
    public static SourceSchema LinkSchema { get; } = Schema(3, "source-otlp-span-links", new
    {
        ordinal = SourceFields.Integer(), flags = SourceFields.Integer(true), traceState = SourceFields.String(true), droppedAttributesCount = SourceFields.Integer(true), attributes = TelemetryJson.AttributeField,
        spanId = SourceFields.Reference(), linkedSpanId = SourceFields.Reference()
    });
    public static SourceSchema LogSchema { get; } = Schema(4, "source-otlp-logs", new
    {
        timeUnixNano = SourceFields.String(true), observedTimeUnixNano = SourceFields.String(true),
        severityNumber = SourceFields.Integer(true), severityText = SourceFields.String(true), bodyJson = SourceFields.String(true),
        resourceJson = SourceFields.String(true), scopeJson = SourceFields.String(true),
        exportAttributes = TelemetryJson.AttributeField, resourceAttributes = TelemetryJson.AttributeField, scopeAttributes = TelemetryJson.AttributeField,
        attributes = TelemetryJson.AttributeField, spanId = SourceFields.Reference(required: false)
    });
    public static SourceSchema MetricSchema { get; } = Schema(5, "source-otlp-metric-points", new
    {
        name = SourceFields.String(), unit = SourceFields.String(true), metricType = SourceFields.String(),
        aggregationTemporality = SourceFields.Integer(true), isMonotonic = SourceFields.Boolean(true),
        startTimeUnixNano = SourceFields.String(true), timeUnixNano = SourceFields.String(true),
        valueInteger = SourceFields.String(true), valueDouble = SourceFields.Number(true),
        resourceJson = SourceFields.String(true), scopeJson = SourceFields.String(true),
        exportAttributes = TelemetryJson.AttributeField, resourceAttributes = TelemetryJson.AttributeField, scopeAttributes = TelemetryJson.AttributeField, metricAttributes = TelemetryJson.AttributeField, dataAttributes = TelemetryJson.AttributeField, attributes = TelemetryJson.AttributeField
    });

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var document = await TelemetryJson.Read(context, cancellationToken);
        var root = TelemetryJson.Object(document.RootElement);
        var signals = SignalNames.Where(x => root.TryGetProperty(x, out _)).ToArray();
        if (signals.Length != 1) throw new InvalidDataException("Expected one native OTLP export request signal.");
        var signal = signals[0];
        var exportAttributes = TelemetryJson.Attributes(root, signal);
        var resourceOrdinal = 0;
        foreach (var resource in TelemetryJson.Array(root, signal))
        {
            var resourceJson = TelemetryJson.Raw(resource, "resource");
            var resourceAttributes = TelemetryJson.Attributes(resource, "scopeSpans", "scopeLogs", "scopeMetrics");
            var scopeOrdinal = 0;
            foreach (var scope in TelemetryJson.Array(resource, signal == "resourceSpans" ? "scopeSpans" : signal == "resourceLogs" ? "scopeLogs" : "scopeMetrics"))
            {
                var scopeJson = TelemetryJson.Raw(scope, "scope");
                var scopeAttributes = TelemetryJson.Attributes(scope, "spans", "logRecords", "metrics");
                var ordinal = 0;
                foreach (var item in TelemetryJson.Array(scope, signal == "resourceSpans" ? "spans" : signal == "resourceLogs" ? "logRecords" : "metrics"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TelemetryJson.Object(item);
                    var position = TelemetryJson.Key(context.CaptureKey, resourceOrdinal.ToString(CultureInfo.InvariantCulture), scopeOrdinal.ToString(CultureInfo.InvariantCulture), ordinal++.ToString(CultureInfo.InvariantCulture));
                    if (signal == "resourceSpans")
                    {
                        var trace = TelemetryJson.Hex(item, "traceId", 32);
                        var span = TelemetryJson.Hex(item, "spanId", 16);
                        var key = SpanKey(trace, span);
                        var start = TelemetryJson.Nanos(item, "startTimeUnixNano");
                        var end = TelemetryJson.Nanos(item, "endTimeUnixNano");
                        if (start.Time > end.Time || (start.Exact is not null && end.Exact is not null && ulong.Parse(end.Exact, CultureInfo.InvariantCulture) != 0 && ulong.Parse(start.Exact, CultureInfo.InvariantCulture) > ulong.Parse(end.Exact, CultureInfo.InvariantCulture)))
                            throw new InvalidDataException("Span ends before it starts.");
                        var refs = new Dictionary<string, SourceReference>();
                        var parent = TelemetryJson.Hex(item, "parentSpanId", 16, false);
                        if (parent.Length != 0) refs.Add("parentSpanId", new("otlp-span", SpanKey(trace, parent)));
                        yield return new("otlp-span", key, Revision(item, SourceJson.Canonical(SourceFields.Of(resourceAttributes)), SourceJson.Canonical(SourceFields.Of(new { exportAttributes, scopeAttributes }))), SpanSchema, SourceFields.Of(new
                        {
                            traceId = trace, spanId = span, name = TelemetryJson.Text(item, "name"),
                            spanKind = Int(item, "kind"), statusCode = StatusCode(item), statusMessage = StatusMessage(item),
                            traceState = OptionalText(item, "traceState"), flags = Int(item, "flags"),
                            droppedAttributesCount = Int(item, "droppedAttributesCount"), droppedEventsCount = Int(item, "droppedEventsCount"), droppedLinksCount = Int(item, "droppedLinksCount"),
                            startTimeUnixNano = start.Exact, endTimeUnixNano = end.Exact, resourceJson, scopeJson, exportAttributes, resourceAttributes, scopeAttributes,
                            attributes = TelemetryJson.Attributes(item)
                        }), refs, start.Time, end.Time);
                        var eventIndex = 0;
                        foreach (var spanEvent in TelemetryJson.Array(item, "events", false))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var at = TelemetryJson.Nanos(spanEvent, "timeUnixNano");
                            var eventKey = TelemetryJson.Key(key, (eventIndex++).ToString(CultureInfo.InvariantCulture));
                            yield return new("otlp-span-event", eventKey, Revision(spanEvent), EventSchema, SourceFields.Of(new
                            {
                                name = TelemetryJson.Text(spanEvent, "name"), ordinal = eventIndex - 1,
                                timeUnixNano = at.Exact, attributes = TelemetryJson.Attributes(spanEvent)
                            }), new Dictionary<string, SourceReference> { ["spanId"] = new("otlp-span", key) }, at.Time, at.Time);
                        }
                        var linkIndex = 0;
                        foreach (var link in TelemetryJson.Array(item, "links", false))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var target = SpanKey(TelemetryJson.Hex(link, "traceId", 32), TelemetryJson.Hex(link, "spanId", 16));
                            var linkKey = TelemetryJson.Key(key, (linkIndex++).ToString(CultureInfo.InvariantCulture));
                            yield return new("otlp-span-link", linkKey, Revision(link), LinkSchema, SourceFields.Of(new
                            {
                                ordinal = linkIndex - 1, flags = Int(link, "flags"), traceState = OptionalText(link, "traceState"), droppedAttributesCount = Int(link, "droppedAttributesCount"), attributes = TelemetryJson.Attributes(link)
                            }), new Dictionary<string, SourceReference> { ["spanId"] = new("otlp-span", key), ["linkedSpanId"] = new("otlp-span", target) });
                        }
                    }
                    else if (signal == "resourceLogs")
                    {
                        var at = TelemetryJson.Nanos(item, "timeUnixNano");
                        var observed = TelemetryJson.Nanos(item, "observedTimeUnixNano");
                        var refs = new Dictionary<string, SourceReference>();
                        var trace = TelemetryJson.Hex(item, "traceId", 32, false);
                        var span = TelemetryJson.Hex(item, "spanId", 16, false);
                        if (span.Length != 0 && trace.Length == 0) throw new InvalidDataException("Log spanId requires traceId.");
                        if (span.Length != 0) refs.Add("spanId", new("otlp-span", SpanKey(trace, span)));
                        yield return new("otlp-log", position, position, LogSchema, SourceFields.Of(new
                        {
                            timeUnixNano = at.Exact, observedTimeUnixNano = observed.Exact,
                            severityNumber = Int(item, "severityNumber"), severityText = OptionalText(item, "severityText"),
                            bodyJson = TelemetryJson.Raw(item, "body"), resourceJson, scopeJson, exportAttributes, resourceAttributes, scopeAttributes, attributes = TelemetryJson.Attributes(item)
                        }), refs, at.Time, at.Time);
                    }
                    else
                    {
                        var types = MetricTypes.Where(x => item.TryGetProperty(x, out _)).ToArray();
                        if (types.Length != 1 || types[0] is not ("gauge" or "sum")) throw new InvalidDataException("Only gauge and sum OTLP metrics are supported.");
                        var metricType = types[0];
                        var data = TelemetryJson.Object(item.GetProperty(metricType));
                        var pointIndex = 0;
                        foreach (var point in TelemetryJson.Array(data, "dataPoints"))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var at = TelemetryJson.Nanos(point, "timeUnixNano");
                            var start = TelemetryJson.Nanos(point, "startTimeUnixNano");
                            if (start.Time > at.Time || (start.Exact is not null && at.Exact is not null && ulong.Parse(at.Exact, CultureInfo.InvariantCulture) != 0 && ulong.Parse(start.Exact, CultureInfo.InvariantCulture) > ulong.Parse(at.Exact, CultureInfo.InvariantCulture))) throw new InvalidDataException("Metric window ends before start.");
                            string? integer = null; double? number = null;
                            if (point.TryGetProperty("asInt", out var integerValue))
                            {
                                integer = integerValue.ValueKind == JsonValueKind.String ? integerValue.GetString() : integerValue.GetRawText();
                                if (!long.TryParse(integer, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)) throw new InvalidDataException("Invalid asInt.");
                            }
                            if (point.TryGetProperty("asDouble", out var numberValue))
                            {
                                if (numberValue.ValueKind != JsonValueKind.Number || !numberValue.TryGetDouble(out var parsed) || !double.IsFinite(parsed)) throw new InvalidDataException("Only finite numeric asDouble is supported.");
                                number = parsed;
                            }
                            if ((integer is null) == (number is null)) throw new InvalidDataException("Metric requires exactly one numeric value.");
                            var key = TelemetryJson.Key(position, (pointIndex++).ToString(CultureInfo.InvariantCulture));
                            yield return new("otlp-metric-point", key, key, MetricSchema, SourceFields.Of(new
                            {
                                name = TelemetryJson.Text(item, "name"), unit = OptionalText(item, "unit"), metricType,
                                aggregationTemporality = Int(data, "aggregationTemporality"), isMonotonic = Bool(data, "isMonotonic"),
                                startTimeUnixNano = start.Exact, timeUnixNano = at.Exact, valueInteger = integer, valueDouble = number,
                                resourceJson, scopeJson, exportAttributes, resourceAttributes, scopeAttributes,
                                metricAttributes = TelemetryJson.Attributes(item, metricType), dataAttributes = TelemetryJson.Attributes(data, "dataPoints"), attributes = TelemetryJson.Attributes(point)
                            }), new Dictionary<string, SourceReference>(), metricType == "gauge" ? at.Time : start.Time, at.Time);
                        }
                    }
                }
                scopeOrdinal++;
            }
            resourceOrdinal++;
        }
    }
    private static string Revision(JsonElement item, string? resource = null, string? scope = null) =>
        SourceJson.Hash(TelemetryJson.Key(SourceJson.Canonical(item), resource ?? "", scope ?? ""));
    private static long? StatusCode(JsonElement item) => item.TryGetProperty("status", out var status) ? Int(TelemetryJson.Object(status), "code") : null;
    private static string? StatusMessage(JsonElement item) => item.TryGetProperty("status", out var status) ? OptionalText(TelemetryJson.Object(status), "message") : null;
    private static string SpanKey(string trace, string span) => TelemetryJson.Key(trace, span);
    private static string? OptionalText(JsonElement value, string name) => value.TryGetProperty(name, out _) ? TelemetryJson.Text(value, name, false) : null;
    private static long? Int(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field)) return null;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out var number)) throw new InvalidDataException("Invalid integer " + name);
        return number;
    }
    private static bool? Bool(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field)) return null;
        if (field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid boolean " + name);
        return field.GetBoolean();
    }
}
