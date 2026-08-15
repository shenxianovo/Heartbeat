using Heartbeat.Desktop.Mac.Input;
using Heartbeat.Collector.System.Input;

namespace Heartbeat.Desktop.Mac.Native;

public static class MacInputNativeEventTranslator
{
    private const uint LeftMouseDown = 1;
    private const uint RightMouseDown = 3;
    private const uint KeyDown = 10;
    private const uint KeyUp = 11;
    private const uint ScrollWheel = 22;
    private const uint OtherMouseDown = 25;

    public static int NormalizeScrollDelta(
        bool continuous,
        long lineDelta,
        long pointDelta) =>
        checked((int)(continuous
            ? pointDelta
            : lineDelta * InputEventBuffer.WheelDelta));

    public static bool TryTranslate(
        uint eventType,
        long keyCode,
        long mouseButton,
        long scrollDelta,
        out MacInputObservation observation)
    {
        switch (eventType)
        {
            case KeyDown:
                observation = new(MacInputObservationKind.KeyDown, checked((int)keyCode));
                return true;
            case KeyUp:
                observation = new(MacInputObservationKind.KeyUp, checked((int)keyCode));
                return true;
            case LeftMouseDown:
                observation = new(MacInputObservationKind.MouseButton, 1);
                return true;
            case RightMouseDown:
                observation = new(MacInputObservationKind.MouseButton, 2);
                return true;
            case OtherMouseDown when mouseButton == 2:
                observation = new(MacInputObservationKind.MouseButton, 3);
                return true;
            case ScrollWheel when scrollDelta != 0:
                observation = new(MacInputObservationKind.Scroll, checked((int)scrollDelta));
                return true;
            default:
                observation = default;
                return false;
        }
    }
}
