using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Server.Services;

public sealed class InputEventIngestContractException(string message) : ArgumentException(message);

/// <summary>
/// InputEvent 的严格摄入边界。HTTP 与内部调用共用，保证未知解释空间不会进入事实库。
/// </summary>
public static class InputEventIngestContract
{
    public static void Validate(IReadOnlyCollection<InputEventItem> events)
    {
        foreach (var item in events)
        {
            if (!InputCodeSets.IsKnown(item.CodeSet))
            {
                throw new InputEventIngestContractException(
                    $"InputEvent CodeSet '{item.CodeSet}' is missing or unsupported.");
            }

            switch (item.EventType)
            {
                case InputEventType.KeyDown
                    when item.CodeSet == InputCodeSets.HeartbeatKeyPositionV1 &&
                         !Enum.IsDefined(typeof(InputKeyPosition), item.Code):
                    throw new InputEventIngestContractException(
                        $"InputEvent Code {item.Code} is not a valid physical key position.");
                case InputEventType.KeyDown:
                    // windows-vk-v1 is historical raw evidence; unmapped VK values remain valid facts.
                    break;
                case InputEventType.MouseButton when item.Code is < 1 or > 3:
                    throw new InputEventIngestContractException("Mouse button Code must be 1, 2, or 3.");
                case InputEventType.MouseScroll when item.Code is < 1 or > 2:
                    throw new InputEventIngestContractException("Mouse scroll Code must be 1 or 2.");
                case InputEventType.MouseButton or InputEventType.MouseScroll:
                    break;
                default:
                    throw new InputEventIngestContractException(
                        $"InputEvent type {(short)item.EventType} is unsupported.");
            }
        }
    }
}
