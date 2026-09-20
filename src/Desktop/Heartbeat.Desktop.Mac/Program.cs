using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Heartbeat.Desktop.UI;

namespace Heartbeat.Desktop.Mac;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Use the macOS desktop host on macOS.");
        var directory = args.Length switch
        {
            0 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heartbeat", "Desktop"),
            2 when args[0] == "--data-directory" => Path.GetFullPath(args[1]),
            _ => throw new ArgumentException("Usage: Heartbeat.Desktop.Mac [--data-directory <path>]"),
        };
        DesktopApplication.IconName = "macos";
        DesktopApplication.CreateRuntime = () =>
        {
            var platform = new MacDesktopPlatform();
            return new DesktopRuntime(new DesktopProfile(directory, platform.Credentials), platform);
        };
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.TryShutdown());
        };
        return AppBuilder.Configure<DesktopApplication>().UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}
