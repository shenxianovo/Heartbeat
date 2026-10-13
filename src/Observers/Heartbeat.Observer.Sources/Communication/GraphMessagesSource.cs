using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Heartbeat.Observers.Sources.Communication;

/// <summary>Imports saved native Microsoft Graph message objects or message list/delta response pages.</summary>
public sealed class GraphMessagesSource : IRecordSource
{
    public string Name => "graph-messages";

    public static SourceSchema Schema { get; } = new(
        Guid.Parse("019a0000-0000-7000-8000-000000000301"),
        "graph-message-revisions", "Microsoft Graph message revision",
        SourceFields.WithIdentity(new
        {
            messageId = SourceFields.String(),
            operation = SourceFields.String(),
            removalReason = SourceFields.String(nullable: true, required: false),
            changeKey = SourceFields.String(nullable: true, required: false),
            subject = SourceFields.String(nullable: true, required: false),
            bodyContent = SourceFields.String(nullable: true, required: false),
            bodyContentType = SourceFields.String(nullable: true, required: false),
            bodyPreview = SourceFields.String(nullable: true, required: false),
            internetMessageId = SourceFields.String(nullable: true, required: false),
            isDraft = SourceFields.Boolean(nullable: true, required: false),
            isRead = SourceFields.Boolean(nullable: true, required: false),
            hasAttachments = SourceFields.Boolean(nullable: true, required: false),
            importance = SourceFields.String(nullable: true, required: false),
            flagStatus = SourceFields.String(nullable: true, required: false),
            fromAddress = SourceFields.String(nullable: true, required: false),
            senderAddress = SourceFields.String(nullable: true, required: false),
            toAddresses = SourceFields.Array(new { type = "string", nullable = false }, nullable: true, required: false),
            ccAddresses = SourceFields.Array(new { type = "string", nullable = false }, nullable: true, required: false),
            bccAddresses = SourceFields.Array(new { type = "string", nullable = false }, nullable: true, required: false),
            conversationId = SourceFields.Reference(required: false),
            parentFolderId = SourceFields.Reference(required: false)
        }));

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Scope);
        using var document = await NativeJson.ReadAsync(context.Input, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Expected a native Graph message object or response page.");
        }

        if (root.TryGetProperty("value", out var messages))
        {
            if (messages.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Graph response 'value' must be an array.");
            }

            foreach (var message in messages.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ReadMessage(message, context);
            }
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ReadMessage(root, context);
        }
    }

    private static SourceRecord ReadMessage(JsonElement message, SourceReadContext context)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Graph message must be an object.");
        }

        var id = NativeJson.RequiredString(message, "id");
        var removed = message.TryGetProperty("@removed", out var removal);
        if (removed && removal.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Graph @removed must be an object.");
        }

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["messageId"] = id,
            ["operation"] = removed ? "removed-from-collection" : "upsert"
        };
        var references = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
        DateTimeOffset? factTime = null;
        if (removed)
        {
            // A delta tombstone can mean move out of a folder. It is not proof of mailbox deletion.
            properties["removalReason"] = NativeJson.OptionalString(removal, "reason");
        }
        else
        {
            foreach (var field in new[] { "subject", "changeKey", "bodyPreview", "internetMessageId", "importance" })
            {
                NativeJson.Copy(message, properties, field, JsonValueKind.String);
            }

            foreach (var field in new[] { "isDraft", "isRead", "hasAttachments" })
            {
                NativeJson.Copy(message, properties, field, JsonValueKind.True);
            }

            if (message.TryGetProperty("body", out var body))
            {
                if (body.ValueKind == JsonValueKind.Null)
                {
                    properties["bodyContent"] = null;
                    properties["bodyContentType"] = null;
                }
                else if (body.ValueKind == JsonValueKind.Object)
                {
                    if (body.TryGetProperty("content", out _))
                        properties["bodyContent"] = NativeJson.OptionalString(body, "content");
                    if (body.TryGetProperty("contentType", out _))
                        properties["bodyContentType"] = NativeJson.OptionalString(body, "contentType");
                }
                else throw new InvalidDataException("Graph body must be an object or null.");
            }

            if (message.TryGetProperty("flag", out var flag))
            {
                if (flag.ValueKind == JsonValueKind.Null) properties["flagStatus"] = null;
                else if (flag.ValueKind == JsonValueKind.Object)
                {
                    if (flag.TryGetProperty("flagStatus", out _))
                        properties["flagStatus"] = NativeJson.OptionalString(flag, "flagStatus");
                }
                else throw new InvalidDataException("Graph flag must be an object or null.");
            }

            Address(message, properties, "from", "fromAddress");
            Address(message, properties, "sender", "senderAddress");
            Addresses(message, properties, "toRecipients", "toAddresses");
            Addresses(message, properties, "ccRecipients", "ccAddresses");
            Addresses(message, properties, "bccRecipients", "bccAddresses");
            AddReference(message, references, context.Scope, "conversationId", "graph-conversation");
            AddReference(message, references, context.Scope, "parentFolderId", "graph-mail-folder");
            // This schema expresses the message revision, not the original receipt/delivery event.
            factTime = NativeJson.GraphTime(message, "lastModifiedDateTime");
        }

        var nativeKey = NativeJson.Key(context.Scope, id);
        var revision = removed ? null : NativeJson.OptionalString(message, "changeKey");
        // Native revisions are replay-stable across page ordering. Unversioned records require capture provenance.
        var evidence = NativeJson.Key(nativeKey, revision is null ? context.CaptureKey : "changeKey:" + revision,
            removed ? "removed" : "upsert", SourceJson.Hash(SourceJson.Canonical(message)));
        return new SourceRecord("graph-message", nativeKey, evidence, Schema,
            JsonSerializer.SerializeToElement(properties), references, factTime, factTime);
    }

    private static void AddReference(JsonElement message, Dictionary<string, SourceReference> references,
        string scope, string field, string kind)
    {
        if (NativeJson.OptionalString(message, field) is { Length: > 0 } value)
            references.Add(field, new SourceReference(kind, NativeJson.Key(scope, value)));
    }

    private static void Address(JsonElement source, Dictionary<string, object?> target, string field, string output)
    {
        if (!source.TryGetProperty(field, out var recipient)) return;
        if (recipient.ValueKind == JsonValueKind.Null) { target[output] = null; return; }
        if (recipient.ValueKind != JsonValueKind.Object || !recipient.TryGetProperty("emailAddress", out var address)
            || address.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Graph {field} must contain emailAddress.");
        target[output] = NativeJson.OptionalString(address, "address");
    }

    private static void Addresses(JsonElement source, Dictionary<string, object?> target, string field, string output)
    {
        if (!source.TryGetProperty(field, out var values)) return;
        if (values.ValueKind == JsonValueKind.Null) { target[output] = null; return; }
        if (values.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"Graph {field} must be an array.");
        var addresses = new List<string>();
        foreach (var recipient in values.EnumerateArray())
        {
            if (recipient.ValueKind != JsonValueKind.Object || !recipient.TryGetProperty("emailAddress", out var address)
                || address.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Graph {field} item must contain emailAddress.");
            addresses.Add(NativeJson.RequiredString(address, "address"));
        }
        target[output] = addresses;
    }
}
