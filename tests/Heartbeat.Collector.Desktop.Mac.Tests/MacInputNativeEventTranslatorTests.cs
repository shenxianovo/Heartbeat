using Heartbeat.Collector.Desktop.Mac.Native;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class MacInputNativeEventTranslatorTests
{
    [Theory]
    [InlineData(false, -3, 0, -3, ScrollUnit.Line)]
    [InlineData(true, 0, 17, 17, ScrollUnit.Point)]
    public void ScrollPreservesNativeSignMagnitudeAndUnit(
        bool continuous, long lineDelta, long pointDelta, double expected, ScrollUnit unit)
    {
        var translated = MacInputNativeEventTranslator.TryTranslate(
            22, 0, 0, continuous, 0, lineDelta, 0, pointDelta, out var observation);

        Assert.True(translated);
        Assert.Equal(MacInputObservationKind.Scroll, observation.Kind);
        Assert.Equal(expected, observation.DeltaY);
        Assert.Equal(unit, observation.ScrollUnit);
    }

    [Fact]
    public void ModifierEventsReachTheTranslator()
    {
        Assert.True(MacInputNativeEventTranslator.TryTranslate(
            12, 0x38, 0, false, 0, 0, 0, 0, out _));
    }

    [Theory]
    [InlineData(0x38, 0x3C, 0x02UL, 0x04UL, 0x20000UL)]
    [InlineData(0x3B, 0x3E, 0x01UL, 0x2000UL, 0x40000UL)]
    [InlineData(0x3A, 0x3D, 0x20UL, 0x40UL, 0x80000UL)]
    [InlineData(0x37, 0x36, 0x08UL, 0x10UL, 0x100000UL)]
    public void ReleasingOneSideDoesNotReleaseOrRepressTheOther(
        int left, int right, ulong leftMask, ulong rightMask, ulong aggregate)
    {
        AssertModifier(left, aggregate | leftMask, MacInputObservationKind.KeyDown);
        AssertModifier(right, aggregate | leftMask | rightMask, MacInputObservationKind.KeyDown);
        AssertModifier(left, aggregate | rightMask, MacInputObservationKind.KeyUp);
        AssertModifier(right, 0, MacInputObservationKind.KeyUp);

        // The current physical flag is authoritative even when earlier events were missed.
        AssertModifier(right, aggregate | rightMask, MacInputObservationKind.KeyDown);
        AssertModifier(right, aggregate | rightMask, MacInputObservationKind.KeyDown);
        AssertModifier(right, aggregate | leftMask, MacInputObservationKind.KeyUp);
    }

    [Fact]
    public void CapsLockUsesPhysicalStatelessFlagInsteadOfTheLockLatch()
    {
        AssertModifier(0x39, 0x10000 | 0x80, MacInputObservationKind.KeyDown);
        AssertModifier(0x39, 0x10000, MacInputObservationKind.KeyUp);
        AssertModifier(0x39, 0x80, MacInputObservationKind.KeyDown);
        AssertModifier(0x39, 0, MacInputObservationKind.KeyUp);
        // A latch-only notification cannot prove a physical press.
        AssertModifier(0x39, 0x10000, MacInputObservationKind.KeyUp);
    }

    private static void AssertModifier(int key, ulong flags, MacInputObservationKind expected)
    {
        Assert.True(MacInputNativeEventTranslator.TryTranslate(
            12, key, 0, false, 0, 0, 0, 0, out var observation, flags));
        Assert.Equal(expected, observation.Kind);
        Assert.Equal(key, observation.Value);
    }

    [Fact]
    public void ZeroScrollIsNotRecorded()
    {
        Assert.False(MacInputNativeEventTranslator.TryTranslate(
            22, 0, 0, false, 0, 0, 0, 0, out _));
    }

    [Fact]
    public void HorizontalScrollIsPreserved()
    {
        Assert.True(MacInputNativeEventTranslator.TryTranslate(
            22, 0, 0, true, 0, 0, -9, 0, out var observation));
        Assert.Equal(-9, observation.DeltaX);
        Assert.Equal(0, observation.DeltaY);
    }
}
