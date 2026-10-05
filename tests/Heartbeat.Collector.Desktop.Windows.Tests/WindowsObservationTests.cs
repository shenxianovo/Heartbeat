using System.Buffers.Binary;
using Heartbeat.Collector.Desktop;
using Heartbeat.Collector.Desktop.Windows;

namespace Heartbeat.Collector.Desktop.Windows.Tests;

public sealed class WindowsObservationTests
{
    [Theory]
    [InlineData(0x1d, 0, InputKeyPosition.ControlLeft, DesktopInputKind.KeyDown)]
    [InlineData(0x1d, 2, InputKeyPosition.ControlRight, DesktopInputKind.KeyDown)]
    [InlineData(0x1d, 3, InputKeyPosition.ControlRight, DesktopInputKind.KeyUp)]
    [InlineData(0x1c, 2, InputKeyPosition.NumpadEnter, DesktopInputKind.KeyDown)]
    [InlineData(0x1e, 0, InputKeyPosition.KeyA, DesktopInputKind.KeyDown)]
    [InlineData(0x45, 2, InputKeyPosition.NumLock, DesktopInputKind.KeyDown)]
    [InlineData(0x47, 0, InputKeyPosition.Numpad7, DesktopInputKind.KeyDown)]
    [InlineData(0x47, 2, InputKeyPosition.Home, DesktopInputKind.KeyDown)]
    public void NativeScanCodePreservesPhysicalPositionAndRelease(ushort code, ushort flags, InputKeyPosition expected, DesktopInputKind kind)
    {
        Assert.Equal(new DesktopInputObservation(kind, (int)expected), WindowsInputTranslator.Keyboard(code, flags));
    }

    [Theory]
    [InlineData(0xff, 0)]
    [InlineData(0x45, 4)]
    [InlineData(0x2a, 2)]
    public void UnknownAndSyntheticPrefixCodesDoNotBecomeKeys(ushort code, ushort flags) =>
        Assert.Null(WindowsInputTranslator.Keyboard(code, flags));

    [Fact]
    public void MousePreservesOneBasedButtonsAndSignedScroll()
    {
        var inputs = WindowsInputTranslator.Mouse(0x0405, unchecked((ushort)-120)).ToArray();
        Assert.Equal(new DesktopInputObservation(DesktopInputKind.MouseButtonDown, 1), inputs[0]);
        Assert.Equal(new DesktopInputObservation(DesktopInputKind.MouseButtonDown, 2), inputs[1]);
        Assert.Equal(new DesktopInputObservation(DesktopInputKind.Scroll, DeltaY: -1, ScrollUnit: ScrollUnit.Line), inputs[2]);
        Assert.Empty(WindowsInputTranslator.Mouse(0, 0));
        Assert.Empty(WindowsInputTranslator.Mouse(0x0400, 0));
    }

    [Fact]
    public void TargetReadsSystemUuidPastOtherStructuresAndStrings()
    {
        var uuid = Guid.NewGuid();
        byte[] data = [0, 3, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, (byte)'x', 0, 0, 1, 24, 0, 0, 0, 0, 0, 0, .. uuid.ToByteArray(), 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length - 8);
        Assert.Equal(uuid.ToString("D"), WindowsTarget.Parse(data));
        Assert.Throws<InvalidDataException>(() => WindowsTarget.Parse(data.AsSpan(0, data.Length - 5)));
    }

    [Fact]
    public void MissingAndPlaceholderHardwareIdentityCannotSilentlyCreateANewTarget()
    {
        Assert.Throws<InvalidDataException>(() => WindowsTarget.Parse([]));
        byte[] data = [0, 3, 0, 0, 26, 0, 0, 0, 1, 24, 0, 0, 0, 0, 0, 0, .. new byte[16], 0, 0];
        Assert.Throws<InvalidDataException>(() => WindowsTarget.Parse(data));
        Array.Fill(data, (byte)0xff, 16, 16);
        Assert.Throws<InvalidDataException>(() => WindowsTarget.Parse(data));
    }
}
