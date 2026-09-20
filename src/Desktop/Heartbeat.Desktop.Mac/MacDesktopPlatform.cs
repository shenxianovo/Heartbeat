using System.Diagnostics;
using System.Text.RegularExpressions;
using Heartbeat.Collector.Desktop;
using Heartbeat.Collector.Desktop.Mac;
using Heartbeat.Collector.Desktop.Mac.Native;

namespace Heartbeat.Desktop.Mac;

public sealed partial class MacDesktopPlatform : IDesktopPlatform
{
    public string CollectorKey => "heartbeat.collector.desktop.macos";
    public string DisplayName => "macOS 桌面";
    public TimeProvider Clock { get; } = new MacContinuousTimeProvider();
    public ICredentialStore Credentials { get; } = new MacKeychain();
    public IDesktopObservationSource CreateObservationSource() => new MacSystemObservationSource();

    public string GetTarget()
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/sbin/ioreg")
        { ArgumentList = { "-rd1", "-c", "IOPlatformExpertDevice" }, RedirectStandardOutput = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("无法读取本机 Target。");
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5000)) { process.Kill(); throw new IOException("读取本机 Target 超时。"); }
        var match = PlatformUuid().Match(output);
        if (process.ExitCode != 0 || !match.Success || !Guid.TryParse(match.Groups[1].Value, out var id) || id == Guid.Empty)
            throw new InvalidDataException("无法确认本机稳定 Target。");
        return id.ToString("D");
    }

    public static void OpenPermissionSettings(ObservationCapability capability)
    {
        if (capability == ObservationCapability.Input)
        {
            using var input = new MacInputMonitoringNative();
            input.RequestAuthorization();
        }
        else if (capability == ObservationCapability.WindowTitle)
        {
            using var accessibility = new MacAccessibilityNative();
            accessibility.RequestProcessTrust();
        }
        var pane = capability == ObservationCapability.Input ? "Privacy_ListenEvent" : "Privacy_Accessibility";
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/open")
        { ArgumentList = { $"x-apple.systempreferences:com.apple.preference.security?{pane}" }, UseShellExecute = false });
    }

    [GeneratedRegex("\\\"IOPlatformUUID\\\"\\s*=\\s*\\\"([0-9A-Fa-f-]+)\\\"")]
    private static partial Regex PlatformUuid();
}
