using Heartbeat.Core.DTOs.Input;
using Heartbeat.Desktop.Mac.Input;

namespace Heartbeat.Desktop.Mac.Tests.Input;

public sealed class MacKeyPositionMapperTests
{
    [Theory]
    [InlineData(0x00, InputKeyPosition.KeyA)]
    [InlineData(0x0C, InputKeyPosition.KeyQ)]
    [InlineData(0x12, InputKeyPosition.Digit1)]
    [InlineData(0x24, InputKeyPosition.Enter)]
    [InlineData(0x37, InputKeyPosition.MetaLeft)]
    [InlineData(0x36, InputKeyPosition.MetaRight)]
    [InlineData(0x41, InputKeyPosition.NumpadDecimal)]
    [InlineData(0x7B, InputKeyPosition.ArrowLeft)]
    public void NativeKeyCode_MapsToStablePhysicalPosition(
        ushort nativeKeyCode,
        InputKeyPosition expected)
    {
        Assert.True(MacKeyPositionMapper.TryMap(nativeKeyCode, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnknownNativeKeyCode_IsNotInvented()
    {
        Assert.False(MacKeyPositionMapper.TryMap(0xFF, out _));
    }
}
