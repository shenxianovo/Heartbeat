namespace Heartbeat.Collector.Desktop.Mac.Native;

public static class MacInputNativeEventTranslator
{
    private const uint LeftMouseDown = 1;
    private const uint RightMouseDown = 3;
    private const uint KeyDown = 10;
    private const uint KeyUp = 11;
    private const uint FlagsChanged = 12;
    private const uint ScrollWheel = 22;
    private const uint OtherMouseDown = 25;

    public static bool TryTranslate(
        uint eventType,
        long keyCode,
        long mouseButton,
        bool continuousScroll,
        long lineDeltaX,
        long lineDeltaY,
        long pointDeltaX,
        long pointDeltaY,
        out MacInputObservation observation,
        ulong eventFlags = 0)
    {
        switch (eventType)
        {
            case FlagsChanged:
                // IOLLEvent.h device-specific flags report each physical modifier independently.
                // Aggregate Shift/Control/etc. cannot distinguish one side being released while
                // the other remains held. Caps Lock's latch is NOT a physical key-down signal;
                // only its stateless device flag is used, never a toggle of remembered state.
                var mask = keyCode switch
                {
                    0x36 => 0x10UL, // right Command
                    0x37 => 0x08UL, // left Command
                    0x38 => 0x02UL, // left Shift
                    0x39 => 0x80UL, // NX_DEVICE_ALPHASHIFT_STATELESS_MASK
                    0x3A => 0x20UL, // left Option
                    0x3B => 0x01UL, // left Control
                    0x3C => 0x04UL, // right Shift
                    0x3D => 0x40UL, // right Option
                    0x3E => 0x2000UL, // right Control
                    _ => 0UL,
                };
                if (mask == 0)
                {
                    observation = default;
                    return false;
                }
                observation = new((eventFlags & mask) != 0
                    ? MacInputObservationKind.KeyDown : MacInputObservationKind.KeyUp,
                    checked((int)keyCode));
                return true;
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
            case OtherMouseDown when mouseButton >= 2:
                observation = new(MacInputObservationKind.MouseButton, checked((int)mouseButton + 1));
                return true;
            case ScrollWheel when continuousScroll
                ? pointDeltaX != 0 || pointDeltaY != 0
                : lineDeltaX != 0 || lineDeltaY != 0:
                observation = new(
                    MacInputObservationKind.Scroll,
                    DeltaX: continuousScroll ? pointDeltaX : lineDeltaX,
                    DeltaY: continuousScroll ? pointDeltaY : lineDeltaY,
                    ScrollUnit: continuousScroll
                        ? global::Heartbeat.Collector.Desktop.Mac.ScrollUnit.Point
                        : global::Heartbeat.Collector.Desktop.Mac.ScrollUnit.Line);
                return true;
            default:
                observation = default;
                return false;
        }
    }
}
