using AppKit;

namespace Heartbeat.Desktop.Mac;

public static class Program
{
    public static void Main(string[] args)
    {
        var directory = args.Length switch
        {
            0 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heartbeat", "Desktop"),
            2 when args[0] == "--data-directory" => Path.GetFullPath(args[1]),
            _ => throw new ArgumentException("Usage: Heartbeat.Desktop.Mac [--data-directory <path>]"),
        };
        NSApplication.Init();
        var platform = new MacDesktopPlatform();
        var runtime = new DesktopRuntime(new DesktopProfile(directory, platform.Credentials), platform);
        using var application = new MacApplication(runtime);
        NSApplication.SharedApplication.Delegate = application;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => NSApplication.SharedApplication.Terminate(null));
        };
        NSApplication.Main([]);
    }
}
