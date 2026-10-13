using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Heartbeat.Observers.Sources.Communication;

namespace Heartbeat.Observers.Sources.Gmail;

/// <summary>Offline import of a saved users.messages.get resource; never performs account access.</summary>
public sealed class GmailMessagesSource : IRecordSource
{
    public string Name => "gmail-messages";
    public static SourceSchema Schema { get; } = GmailNative.MessageSchema(false);
    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        GmailNative.Context(context);
        using var document = await NativeJson.ReadAsync(context.Input, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield return GmailNative.Message(document.RootElement, context, null, cancellationToken);
    }
}

/// <summary>Offline import of a saved users.drafts.get resource, retaining stable draft identity.</summary>
public sealed class GmailDraftsSource : IRecordSource
{
    public string Name => "gmail-drafts";
    public static SourceSchema Schema { get; } = GmailNative.MessageSchema(true);
    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        GmailNative.Context(context);
        using var document = await NativeJson.ReadAsync(context.Input, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        GmailNative.Object(root);
        var id = NativeJson.RequiredString(root, "id");
        if (!root.TryGetProperty("message", out var message)) throw new InvalidDataException("Gmail draft requires message.");
        cancellationToken.ThrowIfCancellationRequested();
        yield return GmailNative.Message(message, context, id, cancellationToken);
    }
}

internal static class GmailNative
{
    internal static object Strings(bool required = false) => SourceFields.Array(new { type = "string", nullable = false }, required: required);
    internal static void Context(SourceReadContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CaptureKey);
    }
    internal static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a native Gmail object.");
    }
    internal static SourceSchema MessageSchema(bool draft) => new(
        Guid.Parse(draft ? "019a0000-0000-7000-8000-000000000502" : "019a0000-0000-7000-8000-000000000501"),
        draft ? "gmail-draft-snapshots" : "gmail-message-snapshots", draft ? "Gmail draft snapshot" : "Gmail message snapshot",
        SourceFields.WithIdentity(new
        {
            messageId = SourceFields.String(), draftId = SourceFields.String(required: false),
            messageEntityId = SourceFields.Reference(required: false), threadEntityId = SourceFields.Reference(required: false),
            historyId = SourceFields.String(required: false), internalDate = SourceFields.String(required: false),
            snippet = SourceFields.String(required: false), labelIds = Strings(), sizeEstimate = SourceFields.Integer(required: false),
            raw = SourceFields.String(required: false),
            classificationLabelValues = SourceFields.Array(new { type = "object", nullable = false, fields = new
            {
                labelId = SourceFields.String(), fields = SourceFields.Array(new { type = "object", nullable = false,
                    fields = new { fieldId = SourceFields.String(), selection = SourceFields.String(required: false) } }, required: false)
            } }, required: false),
            mimeParts = SourceFields.Array(new { type = "object", nullable = false, fields = new
            {
                path = SourceFields.String(), parentPath = SourceFields.String(required: false),
                partId = SourceFields.String(required: false), mimeType = SourceFields.String(required: false), filename = SourceFields.String(required: false),
                headers = SourceFields.Array(new { type = "object", nullable = false, fields = new
                { name = SourceFields.String(), value = SourceFields.String() } }, required: false),
                attachmentId = SourceFields.String(required: false), bodySize = SourceFields.Integer(required: false),
                bodyData = SourceFields.String(required: false)
            } }, required: false)
        }));

    internal static SourceRecord Message(JsonElement message, SourceReadContext context, string? draftId, CancellationToken token)
    {
        Object(message);
        Supported(message, "id", "threadId", "historyId", "internalDate", "snippet", "raw", "sizeEstimate", "labelIds", "payload", "classificationLabelValues");
        var id = NativeJson.RequiredString(message, "id");
        var properties = new Dictionary<string, object?> { ["messageId"] = id };
        var references = new Dictionary<string, SourceReference>();
        foreach (var field in new[] { "historyId", "internalDate", "snippet", "raw" }) CopyString(message, properties, field);
        if (properties.TryGetValue("internalDate", out var date))
        {
            if (!long.TryParse((string?)date, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms))
                throw new InvalidDataException("Gmail internalDate must be epoch milliseconds.");
            try { _ = DateTimeOffset.FromUnixTimeMilliseconds(ms); }
            catch (ArgumentOutOfRangeException error) { throw new InvalidDataException("Gmail internalDate is out of range.", error); }
        }
        if (properties.TryGetValue("raw", out var raw)) ValidateBase64Url((string)raw!);
        CopyInteger(message, properties, "sizeEstimate", "sizeEstimate");
        CopyStrings(message, properties, "labelIds");
        if (message.TryGetProperty("classificationLabelValues", out var classifications))
        {
            if (classifications.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Gmail classificationLabelValues must be an array.");
            var labels = new List<Dictionary<string, object?>>();
            foreach (var label in classifications.EnumerateArray())
            {
                token.ThrowIfCancellationRequested(); Object(label); Supported(label, "labelId", "fields");
                var mapped = new Dictionary<string, object?> { ["labelId"] = NativeJson.RequiredString(label, "labelId") };
                if (label.TryGetProperty("fields", out var fields))
                {
                    if (fields.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Gmail classification fields must be an array.");
                    var values = new List<Dictionary<string, object?>>();
                    foreach (var field in fields.EnumerateArray())
                    {
                        Object(field); Supported(field, "fieldId", "selection");
                        var value = new Dictionary<string, object?> { ["fieldId"] = NativeJson.RequiredString(field, "fieldId") };
                        CopyString(field, value, "selection"); values.Add(value);
                    }
                    mapped.Add("fields", values);
                }
                labels.Add(mapped);
            }
            properties.Add("classificationLabelValues", labels);
        }
        if (NativeJson.OptionalString(message, "threadId") is { Length: > 0 } thread)
            references.Add("threadEntityId", new("gmail-thread", NativeJson.Key(context.Scope, thread)));
        if (draftId is not null)
        {
            properties.Add("draftId", draftId);
            references.Add("messageEntityId", new("gmail-message", NativeJson.Key(context.Scope, id)));
        }
        if (message.TryGetProperty("payload", out var payload))
        {
            var parts = new List<Dictionary<string, object?>>();
            Part(payload, "0", null, parts, token);
            properties.Add("mimeParts", parts);
        }
        var nativeKey = NativeJson.Key(context.Scope, draftId ?? id);
        var body = JsonSerializer.SerializeToElement(properties);
        // A snapshot is not the message creation event. internalDate does not date label or draft edits.
        // Capture provenance distinguishes independent snapshots; replay of the same capture is stable.
        return new(draftId is null ? "gmail-message" : "gmail-draft", nativeKey,
            NativeJson.Key(context.CaptureKey, SourceJson.Hash(SourceJson.Canonical(message))),
            draftId is null ? GmailMessagesSource.Schema : GmailDraftsSource.Schema, body, references);
    }
    private static void Part(JsonElement part, string path, string? parent,
        List<Dictionary<string, object?>> parts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Object(part); Supported(part, "partId", "mimeType", "filename", "headers", "body", "parts");
        var result = new Dictionary<string, object?> { ["path"] = path };
        if (parent is not null) result.Add("parentPath", parent);
        foreach (var field in new[] { "partId", "mimeType", "filename" }) CopyString(part, result, field);
        if (part.TryGetProperty("headers", out var headers))
        {
            if (headers.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Gmail headers must be an ordered array.");
            result.Add("headers", headers.EnumerateArray().Select(header =>
            {
                Object(header); Supported(header, "name", "value");
                return new { name = NativeJson.RequiredString(header, "name"), value = NativeJson.OptionalString(header, "value") ?? throw new InvalidDataException("Header value is required.") };
            }).ToArray());
        }
        if (part.TryGetProperty("body", out var body))
        {
            Object(body); Supported(body, "attachmentId", "size", "data"); CopyString(body, result, "attachmentId"); CopyInteger(body, result, "size", "bodySize");
            if (body.TryGetProperty("data", out _))
            {
                var data = NativeJson.OptionalString(body, "data") ?? throw new InvalidDataException("Gmail data cannot be null.");
                ValidateBase64Url(data); result.Add("bodyData", data);
            }
        }
        parts.Add(result);
        if (!part.TryGetProperty("parts", out var children)) return;
        if (children.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Gmail parts must be an array.");
        var index = 0;
        foreach (var child in children.EnumerateArray()) Part(child, path + "." + (index++).ToString(CultureInfo.InvariantCulture), path, parts, token);
    }
    private static void Supported(JsonElement source, params string[] fields)
    {
        foreach (var property in source.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"Unsupported native Gmail field '{property.Name}'; no lossy message snapshot was imported.");
    }
    internal static void CopyString(JsonElement source, Dictionary<string, object?> target, string field)
    {
        if (source.TryGetProperty(field, out _)) target.Add(field, NativeJson.OptionalString(source, field) ?? throw new InvalidDataException($"Gmail {field} cannot be null."));
    }
    internal static void CopyStrings(JsonElement source, Dictionary<string, object?> target, string field)
    {
        if (!source.TryGetProperty(field, out var values)) return;
        if (values.ValueKind != JsonValueKind.Array || values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
            throw new InvalidDataException($"Gmail {field} must be a string array.");
        target.Add(field, values.Clone());
    }
    private static void CopyInteger(JsonElement source, Dictionary<string, object?> target, string field, string output)
    {
        if (!source.TryGetProperty(field, out var value)) return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0) throw new InvalidDataException($"Gmail {field} must be a nonnegative integer.");
        target.Add(output, number);
    }
    private static void ValidateBase64Url(string value)
    {
        var unpadded = value.TrimEnd('=');
        if (value.Length - unpadded.Length > 2 || (value.Length != unpadded.Length && value.Length % 4 != 0) || unpadded.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') || unpadded.Length % 4 == 1)
            throw new InvalidDataException("Invalid Gmail base64url data.");
        try { _ = Convert.FromBase64String(unpadded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - unpadded.Length % 4) % 4)); }
        catch (FormatException error) { throw new InvalidDataException("Invalid Gmail base64url data.", error); }
    }
}
