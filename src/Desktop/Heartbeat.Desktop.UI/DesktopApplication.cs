using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Heartbeat.Desktop.UI;

public sealed class DesktopApplication : Application
{
    public static Func<DesktopRuntime> CreateRuntime { get; set; } = null!;
    public static string IconName { get; set; } = null!;
    private bool _quitting;

    public override void Initialize()
    {
        Name = "Heartbeat Dev";
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var runtime = CreateRuntime();
            var model = new DesktopViewModel(runtime);
            var window = new MainWindow(model, IconName);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            using var image = AssetLoader.Open(new Uri($"avares://Heartbeat.Desktop.UI/Assets/{IconName}.png"));
            var tray = new TrayIcon { Icon = new WindowIcon(image), ToolTipText = Name, IsVisible = true };
            var menu = new NativeMenu();
            var show = new NativeMenuItem("打开 Heartbeat Dev");
            show.Click += (_, _) => { window.Show(); window.Activate(); };
            var quit = new NativeMenuItem("退出 Heartbeat Dev");
            quit.Click += async (_, _) => await QuitAsync();
            menu.Items.Add(show);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);
            tray.Menu = menu;
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            desktop.ShutdownRequested += (_, e) =>
            {
                if (_quitting) return;
                e.Cancel = true;
                // Run after TryShutdown returns; synchronous cleanup must not re-enter its shutdown guard.
                Dispatcher.UIThread.Post(async () => await QuitAsync());
            };
            window.Opened += async (_, _) =>
            {
                if (runtime.Settings is not null) await model.ToggleAsync();
            };

            async Task QuitAsync()
            {
                if (_quitting) return;
                _quitting = true;
                var exitCode = 0;
                try { await runtime.DisposeAsync(); }
                catch (Exception) { exitCode = 1; Console.Error.WriteLine("Heartbeat did not stop cleanly; pending custody may be unconfirmed."); }
                finally { window.AllowClose = true; tray.Dispose(); desktop.Shutdown(exitCode); }
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
