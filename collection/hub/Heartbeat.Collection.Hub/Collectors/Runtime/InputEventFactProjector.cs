using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public interface IInputEventFactSink
{
    /// <summary>
    /// Idempotently takes durable responsibility for an Event projection before the protocol ACK.
    /// Implementations must throw if the item was not durably retained.
    /// </summary>
    bool TryAccept(
        InputEventItem item,
        bool isReplay,
        ICollectorProjectionCommitFence commitFence);

    void Accept(InputEventItem item, bool isReplay)
    {
        if (!TryAccept(item, isReplay, UnfencedCollectorProjectionCommitFence.Instance))
            throw new InvalidOperationException("The InputEvent projection commit was fenced.");
    }
}

public interface ICollectorProjectionCommitFence : ICollectorDurableCommitFence
{
}

internal sealed class UnfencedCollectorProjectionCommitFence : ICollectorProjectionCommitFence
{
    public static UnfencedCollectorProjectionCommitFence Instance { get; } = new();

    public bool IsFenced => false;

    public bool TryPublishFile(string preparedPath, string authoritativePath)
    {
        File.Move(preparedPath, authoritativePath, overwrite: true);
        return true;
    }
}

internal interface IEventFactProjector
{
    bool Supports(string schemaId, int schemaMajor);

    bool TryProject(
        Guid factId,
        DateTimeOffset occurredAt,
        JsonElement payload,
        out InputEventItem? item);
}

internal sealed class InputEventFactProjector : IEventFactProjector
{
    public bool Supports(string schemaId, int schemaMajor) =>
        schemaId == "heartbeat.input" && schemaMajor == 1;

    public bool TryProject(
        Guid factId,
        DateTimeOffset occurredAt,
        JsonElement payload,
        out InputEventItem? item)
    {
        item = null;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("eventType", out var eventTypeValue) ||
            eventTypeValue.ValueKind != JsonValueKind.String ||
            EventType(eventTypeValue.GetString()) is not { } eventType ||
            !payload.TryGetProperty("codeSet", out var codeSetValue) ||
            codeSetValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(codeSetValue.GetString()) ||
            !payload.TryGetProperty("code", out var codeValue) ||
            !codeValue.TryGetInt16(out var code))
            return false;

        item = new InputEventItem
        {
            Id = factId,
            EventType = eventType,
            CodeSet = codeSetValue.GetString()!,
            Code = code,
            Timestamp = occurredAt
        };
        return true;
    }

    private static InputEventType? EventType(string? value) => value switch
    {
        "keyDown" => InputEventType.KeyDown,
        "mouseButton" => InputEventType.MouseButton,
        "mouseScroll" => InputEventType.MouseScroll,
        _ => null
    };
}
