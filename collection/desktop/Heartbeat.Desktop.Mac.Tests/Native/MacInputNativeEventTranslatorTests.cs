using Heartbeat.Desktop.Mac.Input;
using Heartbeat.Desktop.Mac.Native;

namespace Heartbeat.Desktop.Mac.Tests.Native;

public sealed class MacInputNativeEventTranslatorTests
{
    [Theory]
    [InlineData(10u, 0x00, 0, 0, MacInputObservationKind.KeyDown, 0x00)]
    [InlineData(11u, 0x24, 0, 0, MacInputObservationKind.KeyUp, 0x24)]
    [InlineData(1u, 0, 0, 0, MacInputObservationKind.MouseButton, 1)]
    [InlineData(3u, 0, 0, 0, MacInputObservationKind.MouseButton, 2)]
    [InlineData(25u, 0, 2, 0, MacInputObservationKind.MouseButton, 3)]
    [InlineData(22u, 0, 0, -240, MacInputObservationKind.Scroll, -240)]
    public void CoreGraphicsEvent_IsTranslatedWithoutLeakingNativeShape(
        uint eventType,
        long keyCode,
        long mouseButton,
        long scrollDelta,
        MacInputObservationKind expectedKind,
        int expectedValue)
    {
        Assert.True(MacInputNativeEventTranslator.TryTranslate(
            eventType,
            keyCode,
            mouseButton,
            scrollDelta,
            out var observation));
        Assert.Equal(new MacInputObservation(expectedKind, expectedValue), observation);
    }

    [Fact]
    public void UnsupportedMouseButton_IsIgnored()
    {
        Assert.False(MacInputNativeEventTranslator.TryTranslate(25, 0, 4, 0, out _));
    }

    [Theory]
    [InlineData(false, 2, 17, 240)]
    [InlineData(true, 2, 17, 17)]
    [InlineData(true, -1, -45, -45)]
    public void ScrollDelta_UsesLineNotchesForWheelsAndPixelDeltasForTrackpads(
        bool continuous,
        long lineDelta,
        long pointDelta,
        int expected)
    {
        Assert.Equal(
            expected,
            MacInputNativeEventTranslator.NormalizeScrollDelta(
                continuous,
                lineDelta,
                pointDelta));
    }
}
