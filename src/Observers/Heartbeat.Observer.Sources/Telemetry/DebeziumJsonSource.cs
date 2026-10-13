using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Heartbeat.Observers.Sources.Telemetry;

/// <summary>Debezium PostgreSQL JSON value envelopes, with or without Kafka Connect schema.</summary>
public sealed class DebeziumJsonSource : IRecordSource
{
    public string Name => "debezium-json";
    public static SourceSchema Schema { get; } = new(Guid.Parse("019a0000-0102-7000-8000-000000000001"),
        "source-debezium-changes", "Debezium PostgreSQL change", SourceFields.WithIdentity(new
        {
            connector = SourceFields.String(), server = SourceFields.String(), database = SourceFields.String(),
            tableSchema = SourceFields.String(), table = SourceFields.String(), operation = SourceFields.String(),
            logSequenceNumber = SourceFields.String(true), transactionId = SourceFields.String(true), transactionOrder = SourceFields.Integer(true), collectionOrder = SourceFields.Integer(true),
            transactionNativeId = SourceFields.String(true), transactionEntityId = SourceFields.Reference(required: false),
            sourceTime = SourceFields.String(true), sourceTimeUnit = SourceFields.String(true),
            processingTime = SourceFields.String(true), processingTimeUnit = SourceFields.String(true), beforeJson = SourceFields.String(true), afterJson = SourceFields.String(true),
            sourceAttributes = TelemetryJson.AttributeField, attributes = TelemetryJson.AttributeField
        }));

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var document = await TelemetryJson.Read(context, cancellationToken);
        var root = document.RootElement;
        var events = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        var ordinal = 0;
        foreach (var envelope in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TelemetryJson.Object(envelope);
            var item = envelope.TryGetProperty("payload", out var payload) ? TelemetryJson.Object(payload) : envelope;
            if (!item.TryGetProperty("source", out var source)) throw new InvalidDataException("Debezium change requires source.");
            TelemetryJson.Object(source);
            var connector = TelemetryJson.Text(source, "connector");
            if (connector != "postgresql") throw new InvalidDataException("Only PostgreSQL Debezium envelopes are supported.");
            var server = TelemetryJson.Text(source, "name");
            var database = TelemetryJson.Text(source, "db");
            var tableSchema = TelemetryJson.Text(source, "schema");
            var table = TelemetryJson.Text(source, "table");
            var operation = TelemetryJson.Text(item, "op");
            if (operation is not ("c" or "u" or "d" or "r")) throw new InvalidDataException("Only create, update, delete, and snapshot read events are supported.");
            var before = Row(item, "before"); var after = Row(item, "after");
            if (operation is "c" or "u" or "r" && after is null) throw new InvalidDataException("Change requires an after row.");
            if (operation == "d" && (before is null || after is not null)) throw new InvalidDataException("Delete requires before and null after.");
            var lsn = Numeric(source, "lsn");
            if (lsn is null && operation != "r") throw new InvalidDataException("Non-snapshot PostgreSQL events require source.lsn.");
            var tx = Numeric(source, "txId");
            long? order = null; long? collectionOrder = null; string? transactionNativeId = null;
            var references = new Dictionary<string, SourceReference>();
            if (item.TryGetProperty("transaction", out var transaction) && transaction.ValueKind != JsonValueKind.Null)
            {
                TelemetryJson.Object(transaction);
                order = Order(transaction, "total_order");
                collectionOrder = Order(transaction, "data_collection_order");
                transactionNativeId = TelemetryJson.Text(transaction, "id");
                references.Add("transactionEntityId", new("debezium-transaction", TelemetryJson.Key(server, database, transactionNativeId)));
            }
            var time = Timestamp(source);
            var processingTime = Timestamp(item);
            // A WAL position alone can contain several changes. Only transaction metadata plus
            // total_order identifies an event across captures; otherwise retain capture occurrence.
            var key = transactionNativeId is not null && order is not null
                ? TelemetryJson.Key(server, database, transactionNativeId, order.Value.ToString(CultureInfo.InvariantCulture))
                : TelemetryJson.Key(server, database, tableSchema, table, context.CaptureKey, ordinal.ToString(CultureInfo.InvariantCulture));
            ordinal++;
            yield return new("debezium-change", key, SourceJson.Hash(SourceJson.Canonical(item)), Schema, SourceFields.Of(new
            {
                connector, server, database, tableSchema, table, operation, logSequenceNumber = lsn,
                transactionId = tx, transactionOrder = order, collectionOrder, transactionNativeId, sourceTime = time.Exact, sourceTimeUnit = time.Unit,
                processingTime = processingTime.Exact, processingTimeUnit = processingTime.Unit,
                beforeJson = before, afterJson = after, sourceAttributes = TelemetryJson.Attributes(source), attributes = TelemetryJson.Attributes(item, "before", "after", "source", "op")
            }), references, time.Time, time.Time);
        }
    }

    private static long? Order(JsonElement value, string name)
    {
        var exact = Numeric(value, name);
        if (exact is null) return null;
        if (!long.TryParse(exact, NumberStyles.None, CultureInfo.InvariantCulture, out var order)) throw new InvalidDataException("Transaction order exceeds signed integer range.");
        return order;
    }
    private static string? Row(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var row)) throw new InvalidDataException("Missing Debezium " + name);
        if (row.ValueKind == JsonValueKind.Null) return null;
        TelemetryJson.Object(row); return SourceJson.Canonical(row);
    }
    private static string? Numeric(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new InvalidDataException("Invalid unsigned integer " + name);
        return text;
    }
    private static (DateTimeOffset? Time, string? Exact, string? Unit) Timestamp(JsonElement source)
    {
        foreach (var (name, unit, ticksPerUnit) in new[] { ("ts_ns", "ns", 0L), ("ts_us", "us", 10L), ("ts_ms", "ms", 10000L) })
        {
            var exact = Numeric(source, name);
            if (exact is null) continue;
            var value = ulong.Parse(exact, CultureInfo.InvariantCulture);
            try
            {
                var ticks = ticksPerUnit == 0 ? checked((long)(value / 100)) : checked((long)value * ticksPerUnit);
                return (value == 0 ? null : DateTimeOffset.UnixEpoch.AddTicks(ticks), exact, unit);
            }
            catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
            { throw new InvalidDataException("Debezium source timestamp is out of range.", exception); }
        }
        return (null, null, null);
    }
}
