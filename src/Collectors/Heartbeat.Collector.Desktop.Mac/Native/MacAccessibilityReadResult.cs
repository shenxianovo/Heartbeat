namespace Heartbeat.Collector.Desktop.Mac.Native;

internal static class MacAccessibilityReadResult
{
    public static bool HasValue(int error, nint value)
    {
        // These are normal for an application without a focused window/title attribute.
        if (error is -25205 or -25212) // kAXErrorAttributeUnsupported / kAXErrorNoValue
            return false;
        if (error != 0)
            throw new InvalidOperationException($"AX attribute read failed: {error}.");
        return value != 0;
    }
}
