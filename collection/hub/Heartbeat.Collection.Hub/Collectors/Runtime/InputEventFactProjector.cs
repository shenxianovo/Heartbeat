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

/// <summary>
/// Reconciles a complete startup replay with the durable InputEvent projection in one batch.
/// Implementations must preserve Fact IDs and leave every accepted item durable before returning.
/// </summary>
public interface IInputEventFactReplaySink
{
    void Replay(IReadOnlyList<InputEventItem> items);
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
