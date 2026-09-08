using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Heartbeat.Core.Facts;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

internal static class FactCanonicalization
{
    public static string? ValidateProtocolJson(JsonElement element) => FactJson.Validate(element);

    public static string ContentHash(FactSubmission fact)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaRevision", fact.SchemaRevision);
            writer.WriteString("recordState", fact.RecordState switch
            {
                FactRecordState.Present => "present",
                FactRecordState.Retracted => "retracted",
                _ => throw new InvalidOperationException("Unknown Fact record state cannot be canonicalized.")
            });
            writer.WritePropertyName("time");
            WriteFactTime(
                writer,
                fact.Time,
                value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            if (fact.RecordState == FactRecordState.Present)
            {
                writer.WritePropertyName("payload");
                WriteCanonical(writer, fact.Payload);
            }
            writer.WriteEndObject();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    public static string PublishRequestHash(IReadOnlyList<FactSubmission> facts)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var fact in facts)
            {
                writer.WriteStartObject();
                writer.WriteString("streamId", fact.StreamId);
                writer.WriteNumber("schemaRevision", fact.SchemaRevision);
                writer.WriteString("factId", fact.FactId);
                writer.WriteNumber("revision", fact.Revision);
                if (fact.ObservedAt is { } observedAt)
                    writer.WriteString("observedAt", observedAt.ToString("O", CultureInfo.InvariantCulture));
                else
                    writer.WriteNull("observedAt");
                writer.WriteNumber("recordState", (int)fact.RecordState);
                writer.WritePropertyName("time");
                WriteFactTime(
                    writer,
                    fact.Time,
                    value => value.ToString("O", CultureInfo.InvariantCulture));
                var hasPayload = fact.Payload.ValueKind != JsonValueKind.Undefined;
                writer.WriteBoolean("hasPayload", hasPayload);
                if (hasPayload)
                {
                    writer.WritePropertyName("payload");
                    WriteCanonical(writer, fact.Payload);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    public static long PublishLogicalMessageSize(
        Guid activationId,
        Guid messageId,
        IReadOnlyList<FactSubmission> facts)
    {
        using var counter = new CountingWriteStream();
        using (var writer = new Utf8JsonWriter(counter))
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", "heartbeat.collector/1");
            writer.WriteString("type", "facts.publish");
            writer.WriteString("messageId", messageId);
            writer.WriteString("activationId", activationId);
            writer.WritePropertyName("body");
            writer.WriteStartObject();
            writer.WritePropertyName("facts");
            writer.WriteStartArray();
            foreach (var fact in facts)
            {
                writer.WriteStartObject();
                writer.WriteString("streamId", fact.StreamId);
                writer.WriteNumber("schemaRevision", fact.SchemaRevision);
                writer.WriteString("factId", fact.FactId);
                writer.WriteNumber("revision", fact.Revision);
                if (fact.ObservedAt is { } observedAt)
                    writer.WriteString("observedAt", FormatProtocolTimestamp(observedAt));
                writer.WriteString("recordState", fact.RecordState switch
                {
                    FactRecordState.Present => "present",
                    FactRecordState.Retracted => "retracted",
                    _ => throw new InvalidOperationException("Unknown Fact record state cannot be canonicalized.")
                });
                writer.WritePropertyName("time");
                WriteFactTime(writer, fact.Time, FormatProtocolTimestamp);
                if (fact.RecordState == FactRecordState.Present)
                {
                    writer.WritePropertyName("payload");
                    WriteCanonical(writer, fact.Payload);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return counter.BytesWritten;
    }

    private static void WriteFactTime(
        Utf8JsonWriter writer,
        FactTime time,
        Func<DateTimeOffset, string> formatTimestamp)
    {
        writer.WriteStartObject();
        if (time.OccurredAt is { } occurredAt)
        {
            writer.WriteString("occurredAt", formatTimestamp(occurredAt));
        }
        else if (time.Start is { } start && time.End is { } end && time.IsFinal is { } isFinal)
        {
            writer.WriteString("start", formatTimestamp(start));
            writer.WriteString("end", formatTimestamp(end));
            writer.WriteBoolean("isFinal", isFinal);
        }
        else
        {
            throw new InvalidOperationException("Fact time cannot be canonically represented.");
        }
        writer.WriteEndObject();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element) =>
        FactJson.WriteCanonical(writer, element);

    private static string FormatProtocolTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private sealed class CountingWriteStream : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => AddBytes(count);

        public override void Write(ReadOnlySpan<byte> buffer) => AddBytes(buffer.Length);

        public override void WriteByte(byte value) => AddBytes(1);

        private void AddBytes(int count) => BytesWritten = checked(BytesWritten + count);
    }
}
