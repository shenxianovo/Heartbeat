using AppKit;
using CoreGraphics;
using Foundation;

namespace Heartbeat.Desktop.Mac;

public sealed class MacWindow : NSWindow
{
    private readonly DesktopRuntime _runtime;
    private readonly MacConnectionView _connection;
    private readonly MacCollectionView _collection;
    private readonly NSTextField _message;
    private readonly NSButton _replay;
    private readonly NSTimer _timer;
    private bool _performing;
    private bool _quitting;
    private string? _actionError;

    public MacWindow(DesktopRuntime runtime)
        : base(new CGRect(0, 0, 760, 650), NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable,
            NSBackingStore.Buffered, false)
    {
        _runtime = runtime;
        Title = "Heartbeat Dev";
        Center();
        var content = ContentView!;
        var title = MacControls.Label(content, "Heartbeat Dev", 28, 594, 450, 32);
        title.Font = NSFont.BoldSystemFontOfSize(24)!;
        _replay = MacControls.Button(content, "打开时间线 ↗", 574, 592, 160, OpenReplay);
        var tabs = new NSTabView(new CGRect(24, 78, 712, 500));
        _collection = new MacCollectionView(runtime, PerformAsync);
        _connection = new MacConnectionView(runtime, PerformAsync);
        tabs.Add(new NSTabViewItem { Label = "采集", View = _collection });
        tabs.Add(new NSTabViewItem { Label = "连接设置", View = _connection });
        tabs.SelectAt(runtime.Settings is null ? 1 : 0);
        content.AddSubview(tabs);
        _message = MacControls.Label(content, string.Empty, 28, 14, 704, 52);
        WindowShouldClose = _ => { OrderOut(null); return false; };
        _timer = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromSeconds(1), _ => Refresh());
        Refresh();
    }

    public async Task PerformAsync(Func<Task> action)
    {
        if (_quitting || _performing) return;
        _performing = true;
        _actionError = null;
        Refresh();
        string? error = null;
        try { await Task.Run(action); }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            BeginInvokeOnMainThread(() =>
            {
                _actionError = error;
                _performing = false;
                if (!_quitting) Refresh();
            });
        }
    }

    private void OpenReplay()
    {
        if (_runtime.Settings is not { } settings) return;
        using var url = new NSUrl(settings.WebUrl.AbsoluteUri);
        if (!NSWorkspace.SharedWorkspace.OpenUrl(url))
        {
            _actionError = "无法打开默认浏览器。";
            Refresh();
        }
    }

    private void Refresh()
    {
        var enabled = !_performing && !_runtime.IsBusy && !_quitting;
        _connection.Refresh(_runtime.Settings, enabled);
        _collection.Refresh(_runtime, enabled);
        _replay.Enabled = enabled && _runtime.Settings is not null;
        _message.StringValue = _actionError ?? _runtime.Error ?? (_performing ? "正在处理…" : string.Empty);
    }

    public void PrepareToQuit()
    {
        _quitting = true;
        _timer.Invalidate();
        Refresh();
    }
}
