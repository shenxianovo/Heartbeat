using AppKit;
using Foundation;

namespace Heartbeat.Desktop.Mac;

[Register("HeartbeatApplication")]
public sealed class MacApplication(DesktopRuntime runtime) : NSApplicationDelegate
{
    private MacWindow? _window;
    private NSStatusItem? _status;
    private bool _quitting;

    public override void DidFinishLaunching(NSNotification notification)
    {
        var app = NSApplication.SharedApplication;
        app.ActivationPolicy = NSApplicationActivationPolicy.Regular;
        _window = new MacWindow(runtime);
        app.MainMenu = CreateMainMenu();
        _status = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Square);
        // The HIG asks menu-bar extras to use a monochrome template image so the
        // system can tint it for light/dark/inverted menu bars.
        var statusIcon = NSImage.GetSystemSymbol("waveform.path.ecg", "Heartbeat");
        if (statusIcon is not null)
        {
            statusIcon.Template = true;
            _status.Button!.Image = statusIcon;
        }
        else
        {
            _status.Button!.Title = "♡";
        }
        _status.Button.ToolTip = "Heartbeat Dev";
        _status.Menu = new NSMenu();
        _status.Menu.AddItem(new NSMenuItem("打开 Heartbeat Dev", "o", (_, _) => ShowWindow()));
        _status.Menu.AddItem(NSMenuItem.SeparatorItem);
        _status.Menu.AddItem(new NSMenuItem("退出 Heartbeat Dev", "q", (_, _) => app.Terminate(null)));
        ShowWindow();
        _ = _window.PerformAsync(runtime.InitializeAsync);
    }

    private NSMenu CreateMainMenu()
    {
        var menu = new NSMenu();
        var applicationMenu = new NSMenu();
        applicationMenu.AddItem(new NSMenuItem("退出 Heartbeat Dev", "q", (_, _) => NSApplication.SharedApplication.Terminate(null)));
        menu.AddItem(new NSMenuItem("Heartbeat Dev") { Submenu = applicationMenu });
        var edit = new NSMenu();
        edit.AddItem(new NSMenuItem("撤销", new ObjCRuntime.Selector("undo:"), "z"));
        edit.AddItem(new NSMenuItem("剪切", new ObjCRuntime.Selector("cut:"), "x"));
        edit.AddItem(new NSMenuItem("复制", new ObjCRuntime.Selector("copy:"), "c"));
        edit.AddItem(new NSMenuItem("粘贴", new ObjCRuntime.Selector("paste:"), "v"));
        edit.AddItem(new NSMenuItem("全选", new ObjCRuntime.Selector("selectAll:"), "a"));
        menu.AddItem(new NSMenuItem("编辑") { Submenu = edit });

        // "View" carries the standard macOS "Toggle Sidebar" affordance
        // (⌘⌥S). Owning the shortcut here keeps it discoverable even when
        // the sidebar has been collapsed to icons only.
        var view = new NSMenu("显示");
        var toggleSidebar = new NSMenuItem("收起 / 展开侧边栏", "s", (_, _) => _window?.ToggleSidebar())
        {
            KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask,
        };
        view.AddItem(toggleSidebar);
        menu.AddItem(new NSMenuItem("显示") { Submenu = view });

        return menu;
    }

    private void ShowWindow()
    {
        if (_quitting) return;
        _window?.MakeKeyAndOrderFront(null);
        NSApplication.SharedApplication.Activate();
    }

    public override bool ApplicationShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
    {
        ShowWindow();
        return false;
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => false;

    public override NSApplicationTerminateReply ApplicationShouldTerminate(NSApplication sender)
    {
        if (!_quitting)
        {
            _quitting = true;
            _window?.PrepareToQuit();
            // Defer the reply until AppKit has received Later, even for synchronous cleanup.
            sender.BeginInvokeOnMainThread(() => _ = QuitAsync(sender));
        }
        return NSApplicationTerminateReply.Later;
    }

    private async Task QuitAsync(NSApplication app)
    {
        try { await Task.Run(async () => await runtime.DisposeAsync()); }
        catch (Exception)
        {
            Environment.ExitCode = 1;
            Console.Error.WriteLine("Heartbeat did not stop cleanly; pending custody may be unconfirmed.");
        }
        finally
        {
            app.BeginInvokeOnMainThread(() =>
            {
                if (_status is not null) NSStatusBar.SystemStatusBar.RemoveStatusItem(_status);
                app.ReplyToApplicationShouldTerminate(true);
            });
        }
    }
}
