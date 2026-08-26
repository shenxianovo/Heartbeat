using System.Diagnostics;
using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.Windows;

internal static class WindowsBrowserSetupLauncher
{
    public static void Open(BrowserKind browser, string sideloadDirectory)
    {
        if (!Directory.Exists(sideloadDirectory))
            throw new DirectoryNotFoundException($"浏览器采集器目录不存在：{sideloadDirectory}");

        var executable = ResolveExecutable(browser) ?? throw new InvalidOperationException(
            $"未找到已安装的 {(browser == BrowserKind.Edge ? "Microsoft Edge" : "Google Chrome")}。");
        var url = browser == BrowserKind.Edge ? "edge://extensions" : "chrome://extensions";
        var browserStart = new ProcessStartInfo(executable) { UseShellExecute = false };
        browserStart.ArgumentList.Add(url);
        if (Process.Start(browserStart) is null)
            throw new InvalidOperationException("无法打开浏览器扩展管理页。");

        if (Process.Start(new ProcessStartInfo(sideloadDirectory) { UseShellExecute = true }) is null)
            throw new InvalidOperationException("无法在文件资源管理器中打开采集器目录。");
    }

    internal static string? ResolveExecutable(BrowserKind browser)
    {
        var executable = browser == BrowserKind.Edge ? "msedge.exe" : "chrome.exe";
        var vendorPath = browser == BrowserKind.Edge
            ? Path.Combine("Microsoft", "Edge", "Application", executable)
            : Path.Combine("Google", "Chrome", "Application", executable);
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), vendorPath),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), vendorPath),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), vendorPath),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
