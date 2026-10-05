using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Heartbeat.Desktop.Windows;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var directory = args.Length switch
        {
            0 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heartbeat", "Desktop"),
            2 when args[0] == "--data-directory" => Path.GetFullPath(args[1]),
            _ => throw new ArgumentException("Usage: Heartbeat.Desktop.Windows [--data-directory <path>]"),
        };
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new WindowsApplication(directory);
        });
    }
}

internal sealed class WindowsApplication(string directory) : Application
{
    private DesktopWindow? _window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        var platform = new WindowsDesktopPlatform();
        var runtime = new DesktopRuntime(new DesktopProfile(directory, platform.Credentials), platform);
        _window = new DesktopWindow(runtime);
        _window.Activate();
        _ = _window.PerformAsync(runtime.InitializeAsync);
    }
}
