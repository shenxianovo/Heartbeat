using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.Mac.Native;

public interface IMacBrowserSetupLauncher
{
    void Open(BrowserKind browser, string sideloadDirectory);
}

public sealed class MacBrowserSetupLauncher(IMacCommandRunner commands) : IMacBrowserSetupLauncher
{
    public void Open(BrowserKind browser, string sideloadDirectory)
    {
        if (!Directory.Exists(sideloadDirectory))
            throw new DirectoryNotFoundException($"浏览器采集器目录不存在：{sideloadDirectory}");

        var applicationName = browser == BrowserKind.Edge ? "Microsoft Edge" : "Google Chrome";
        var url = browser == BrowserKind.Edge ? "edge://extensions" : "chrome://extensions";
        var browserResult = commands.Run("/usr/bin/open", ["-a", applicationName, url]);
        if (browserResult.ExitCode != 0)
            throw new InvalidOperationException($"无法打开 {applicationName}：{browserResult.StandardError.Trim()}");

        var finderResult = commands.Run("/usr/bin/open", [sideloadDirectory]);
        if (finderResult.ExitCode != 0)
            throw new InvalidOperationException($"无法在 Finder 中打开采集器目录：{finderResult.StandardError.Trim()}");
    }
}
