using Heartbeat.Desktop.Mac.Native;

namespace Heartbeat.Desktop.Mac.Tests.Native;

public sealed class MacAccessibilityNativeTests
{
    [Fact]
    public void TrustedRealDevice_CanReadAndAttachWithoutRequestingPermission()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var workspace = new CocoaWorkspaceNative();
        using var accessibility = new MacAccessibilityNative();
        Assert.True(accessibility.IsAvailable);

        var application = workspace.FrontmostApplication;
        var processIdentifier = application?.ProcessIdentifier ?? 0;
        if (!accessibility.IsProcessTrusted || processIdentifier <= 0)
            return;

        _ = accessibility.ReadFocusedWindowTitle(processIdentifier);
        accessibility.ObserveApplication(processIdentifier);
        accessibility.StopObserving();
    }
}
