using Heartbeat.Collector.Desktop;
using Heartbeat.Collector.Desktop.Windows;

namespace Heartbeat.Desktop.Windows;

public sealed class WindowsDesktopPlatform : IDesktopPlatform
{
    public string CollectorKey => "heartbeat.collector.desktop.windows";
    public string DisplayName => "Windows 桌面";
    public string GetTarget() => WindowsTarget.Read();
    public TimeProvider Clock => TimeProvider.System;
    public ICredentialStore Credentials { get; } = new WindowsCredentials();
    public IDesktopObservationSource CreateObservationSource() => new WindowsSystemObservationSource();
}
