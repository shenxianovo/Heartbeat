using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Heartbeat.Observers.Sources.Telemetry;

/// <summary>CloudEvents 1.0 structured JSON event or JSON batch; no HTTP binary binding.</summary>
public sealed class CloudEventsJsonSource : IRecordSource
{
    public string Name => "cloudevents-json";
    public static SourceSchema Schema { get; } = new(Guid.Parse("019a0000-0100-7000-8000-000000000001"),
        "source-cloud-events", "CloudEvents structured event", SourceFields.WithIdentity(new
        {
            eventId = SourceFields.String(), source = SourceFields.String(), eventType = SourceFields.String(),
            exactTime = SourceFields.String(nullable: true), attributes = TelemetryJson.AttributeField
        }));

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var document = await TelemetryJson.Read(context, cancellationToken);
        var root = document.RootElement;
        var events = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TelemetryJson.Text(item, "specversion") != "1.0") throw new InvalidDataException("Only CloudEvents 1.0 is supported.");
            var id = TelemetryJson.Text(item, "id");
            var source = TelemetryJson.Text(item, "source");
            if (!Uri.TryCreate(source, UriKind.RelativeOrAbsolute, out _)) throw new InvalidDataException("Invalid CloudEvents source URI-reference.");
            var type = TelemetryJson.Text(item, "type");
            var exactTime = item.TryGetProperty("time", out var timeValue) && timeValue.ValueKind != JsonValueKind.Null ? TelemetryJson.Text(item, "time") : null;
            var time = TelemetryJson.Rfc3339(exactTime);
            if (item.TryGetProperty("data_base64", out _))
            {
                if (item.TryGetProperty("data", out _)) throw new InvalidDataException("data and data_base64 are mutually exclusive.");
                try { Convert.FromBase64String(TelemetryJson.Text(item, "data_base64")); }
                catch (FormatException exception) { throw new InvalidDataException("Invalid CloudEvents data_base64.", exception); }
            }
            var key = TelemetryJson.Key(source, id);
            yield return new SourceRecord("cloudevent", key, key, Schema,
                SourceFields.Of(new { eventId = id, source, eventType = type, exactTime, attributes = TelemetryJson.Attributes(item, item.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Null && p.Name != "data").Select(p => p.Name).ToArray()) }),
                new Dictionary<string, SourceReference>(), time, time);
        }
    }
}
