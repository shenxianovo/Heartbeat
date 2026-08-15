using Heartbeat.Desktop.Mac.Native;

namespace Heartbeat.Desktop.Mac.Tests.Native;

public sealed class MacInputMonitoringNativeTests
{
    [Fact]
    public void AuthorizedRealDevice_CanStartAndStopWithoutRequestingPermission()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var native = new MacInputMonitoringNative();
        Assert.True(native.IsAvailable);
        if (!native.IsAuthorized) return;

        native.StartListening();
        native.StopListening();
    }
}
