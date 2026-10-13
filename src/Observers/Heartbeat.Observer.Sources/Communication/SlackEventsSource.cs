using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Heartbeat.Observers.Sources.Communication;

/// <summary>Imports saved native Slack Events API event_callback envelopes for message events.</summary>
public sealed class SlackEventsSource : IRecordSource
{
    public string Name => "slack-events";

    public static SourceSchema Schema { get; } = new(
        Guid.Parse("019a0000-0000-7000-8000-000000000302"),
        "slack-message-revisions", "Slack message event revision",
        SourceFields.WithIdentity(new
        {
            workspaceId = SourceFields.String(),
            channel = SourceFields.String(),
            messageTs = SourceFields.String(),
            eventId = SourceFields.String(),
            operation = SourceFields.String(),
            text = SourceFields.String(nullable: true, required: false),
            previousText = SourceFields.String(nullable: true, required: false),
            channelId = SourceFields.Reference(),
            authorId = SourceFields.Reference(required: false),
            threadRootId = SourceFields.Reference(required: false)
        }));

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Scope);
        using var document = await NativeJson.ReadAsync(context.Input, cancellationToken).ConfigureAwait(false);
        var envelope = document.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object || NativeJson.RequiredString(envelope, "type") != "event_callback")
            throw new InvalidDataException("Expected a native Slack event_callback envelope.");
        var workspace = NativeJson.RequiredString(envelope, "team_id");
        var eventId = NativeJson.RequiredString(envelope, "event_id");
        if (!envelope.TryGetProperty("event", out var notification) || notification.ValueKind != JsonValueKind.Object
            || NativeJson.RequiredString(notification, "type") != "message")
            throw new InvalidDataException("Only Slack message Events API callbacks are supported.");
        var channel = NativeJson.RequiredString(notification, "channel");
        var subtype = NativeJson.OptionalString(notification, "subtype");
        if (subtype is not (null or "message_changed" or "message_deleted"))
            throw new InvalidDataException($"Slack message subtype '{subtype}' is not supported.");

        var changed = subtype == "message_changed";
        var deleted = subtype == "message_deleted";
        var message = notification;
        if (changed && (!notification.TryGetProperty("message", out message) || message.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Slack message_changed must contain a message object.");
        var ts = NativeJson.RequiredString(deleted ? notification : message, deleted ? "deleted_ts" : "ts");
        _ = NativeJson.SlackTime(ts); // Validate identity format without using creation time for edit/delete facts.
        var key = NativeJson.Key(context.Scope, workspace, channel, ts);
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["workspaceId"] = workspace, ["channel"] = channel, ["messageTs"] = ts,
            ["eventId"] = eventId, ["operation"] = deleted ? "delete" : changed ? "edit" : "create"
        };
        var references = new Dictionary<string, SourceReference>(StringComparer.Ordinal)
        {
            ["channelId"] = new("slack-channel", NativeJson.Key(context.Scope, workspace, channel))
        };
        if (!deleted)
        {
            NativeJson.Copy(message, properties, "text", JsonValueKind.String);
            if (NativeJson.OptionalString(message, "user") is { Length: > 0 } user)
                references["authorId"] = new("slack-user", NativeJson.Key(context.Scope, workspace, user));
            if (NativeJson.OptionalString(message, "thread_ts") is { Length: > 0 } thread)
            {
                _ = NativeJson.SlackTime(thread);
                references["threadRootId"] = new("slack-message", NativeJson.Key(context.Scope, workspace, channel, thread));
            }
        }
        if (notification.TryGetProperty("previous_message", out var previous))
        {
            if (previous.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Slack previous_message must be an object.");
            if (previous.TryGetProperty("text", out _))
                properties["previousText"] = NativeJson.OptionalString(previous, "text");
        }

        string? factTimestamp;
        if (deleted) factTimestamp = NativeJson.OptionalString(notification, "event_ts");
        else if (changed)
        {
            factTimestamp = null;
            if (message.TryGetProperty("edited", out var edited))
            {
                if (edited.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Slack edited must be an object.");
                factTimestamp = NativeJson.OptionalString(edited, "ts");
            }
            // event_ts is the native edit event time; envelope event_time is dispatch metadata.
            factTimestamp ??= NativeJson.OptionalString(notification, "event_ts");
        }
        else factTimestamp = ts;
        var factTime = NativeJson.SlackTime(factTimestamp);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new SourceRecord("slack-message", key, NativeJson.Key(workspace, eventId), Schema,
            JsonSerializer.SerializeToElement(properties), references, factTime, factTime);
    }
}
