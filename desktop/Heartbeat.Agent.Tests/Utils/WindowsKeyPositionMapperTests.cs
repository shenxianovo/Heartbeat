using Heartbeat.Agent.Utils;
using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Agent.Tests.Utils;

public sealed class WindowsKeyPositionMapperTests
{
    [Theory]
    [InlineData(0x41u)]
    [InlineData(0x51u)]
    [InlineData(0xE9u)]
    public void ScanCodeDeterminesPosition_RegardlessOfVirtualKey(uint virtualKey)
    {
        Assert.True(WindowsKeyPositionMapper.TryMap(
            new WindowsNativeKeyObservation(virtualKey, 0x1E, false),
            out var position));
        Assert.Equal(InputKeyPosition.KeyA, position);
    }

    [Theory]
    [InlineData(0x02u, false, InputKeyPosition.Digit1)]
    [InlineData(0x1Du, false, InputKeyPosition.ControlLeft)]
    [InlineData(0x1Du, true, InputKeyPosition.ControlRight)]
    [InlineData(0x5Bu, true, InputKeyPosition.MetaLeft)]
    [InlineData(0x1Cu, true, InputKeyPosition.NumpadEnter)]
    public void MapsNativePhysicalPositions(uint scanCode, bool extended, InputKeyPosition expected)
    {
        Assert.True(WindowsKeyPositionMapper.TryMap(
            new WindowsNativeKeyObservation(0, scanCode, extended),
            out var position));
        Assert.Equal(expected, position);
    }

    [Theory]
    [InlineData(0x13u, InputKeyPosition.Pause)]
    [InlineData(0x90u, InputKeyPosition.NumLock)]
    public void SharedScanCode45_UsesVirtualKeyOnlyForExceptionalDisambiguation(
        uint virtualKey,
        InputKeyPosition expected)
    {
        Assert.True(WindowsKeyPositionMapper.TryMap(
            new WindowsNativeKeyObservation(virtualKey, 0x45, false),
            out var position));
        Assert.Equal(expected, position);
    }

    [Fact]
    public void UnknownScanCode_IsNotPersistedAsGuessedPosition()
        => Assert.False(WindowsKeyPositionMapper.TryMap(
            new WindowsNativeKeyObservation(0x41, 0, false),
            out _));
}
