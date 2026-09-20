using System.Diagnostics;
using Heartbeat.Collector.Desktop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Heartbeat.Desktop.Windows;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The native tray is removed in QuitAsync before the WinUI window closes.")]
internal sealed class DesktopWindow : Window
{
    private readonly DesktopRuntime _runtime;
    private readonly TextBlock _status = new() { FontSize = 22 };
    private readonly TextBlock _delivery = new();
    private readonly TextBlock _capabilities = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _toggle = new();
    private readonly Button _replay = new() { Content = "打开时间线 ↗" };
    private readonly Button _save = new() { Content = "验证并保存" };
    private readonly TextBox _backend = new() { Header = "后端地址" };
    private readonly TextBox _auth = new() { Header = "Auth 地址" };
    private readonly TextBox _web = new() { Header = "Web 时间线地址" };
    private readonly PasswordBox _key = new() { Header = "API key", PlaceholderText = "留空使用已保存的凭据" };
    private readonly TextBlock _owner = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherQueueTimer _timer;
    private readonly WindowsTray _tray;
    private bool _performing, _quitting;
    private string? _actionError;

    public DesktopWindow(DesktopRuntime runtime)
    {
        _runtime = runtime;
        Title = "Heartbeat Dev";
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(800, 800));
        var content = new StackPanel { Padding = new Thickness(28), Spacing = 20 };
        content.Children.Add(new TextBlock { Text = "Heartbeat Dev", FontSize = 28 });
        _replay.Click += (_, _) => OpenReplay();
        content.Children.Add(_replay);
        var tabs = new Pivot();
        tabs.Items.Add(new PivotItem { Header = "采集", Content = Collection() });
        tabs.Items.Add(new PivotItem { Header = "连接设置", Content = Connection() });
        tabs.SelectedIndex = runtime.Settings is null ? 1 : 0;
        content.Children.Add(tabs);
        content.Children.Add(_message);
        Content = new ScrollViewer { Content = content };
        _tray = new WindowsTray(WinRT.Interop.WindowNative.GetWindowHandle(this), Show, () => _ = QuitAsync());
        AppWindow.Closing += (_, args) =>
        {
            if (_quitting) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private StackPanel Collection()
    {
        var panel = new StackPanel { Spacing = 18 };
        panel.Children.Add(_status);
        panel.Children.Add(new TextBlock { Text = "关闭窗口后继续运行，可从系统托盘重新打开。", TextWrapping = TextWrapping.Wrap });
        _toggle.Click += async (_, _) =>
        {
            var collecting = _runtime.IsCollecting;
            await PerformAsync(collecting ? _runtime.StopCollectionAsync : _runtime.StartAsync);
        };
        panel.Children.Add(_toggle);
        panel.Children.Add(new TextBlock { Text = "系统观察能力", FontSize = 18 });
        panel.Children.Add(_capabilities);
        panel.Children.Add(new TextBlock { Text = "只记录非文本物理输入事件，不记录输入的文字。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_delivery);
        panel.Children.Add(new TextBlock { Text = "已接管的数据保存在本地，连接恢复后继续上传。暂停采集不影响交付。", TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private StackPanel Connection()
    {
        _backend.Text = _runtime.Settings?.BackendUrl.AbsoluteUri ?? "http://localhost:8080";
        _auth.Text = _runtime.Settings?.AuthUrl.AbsoluteUri ?? "https://auth.shenxianovo.com";
        _web.Text = _runtime.Settings?.WebUrl.AbsoluteUri ?? "http://localhost:3000";
        var panel = new StackPanel { Spacing = 12 };
        foreach (var field in new UIElement[] { _backend, _auth, _web, _key, _owner, _save }) panel.Children.Add(field);
        panel.Children.Add(new TextBlock { Text = "API key 保存到 Windows 凭据管理器。首次接入需要联网验证账号。", TextWrapping = TextWrapping.Wrap });
        _save.Click += async (_, _) =>
        {
            var backend = _backend.Text;
            var auth = _auth.Text;
            var web = _web.Text;
            var key = _key.Password;
            await PerformAsync(async () =>
            {
                await _runtime.ConfigureAsync(new Uri(backend), new Uri(auth), new Uri(web), key);
                DispatcherQueue.TryEnqueue(() => _key.Password = string.Empty);
            });
        };
        return panel;
    }

    public async Task PerformAsync(Func<Task> action)
    {
        if (_performing || _quitting) return;
        _performing = true;
        _actionError = null;
        Refresh();
        try { await Task.Run(action); }
        catch (Exception exception) { _actionError = exception.Message; }
        finally { _performing = false; if (!_quitting) Refresh(); }
    }

    private void Refresh()
    {
        var enabled = !_performing && !_runtime.IsBusy && !_quitting;
        _toggle.IsEnabled = _save.IsEnabled = enabled;
        _backend.IsEnabled = _auth.IsEnabled = _web.IsEnabled = _key.IsEnabled = enabled;
        _replay.IsEnabled = enabled && _runtime.Settings is not null;
        _status.Text = _runtime.IsCollecting ? "正在采集" : "采集已暂停";
        _toggle.Content = _runtime.IsCollecting ? "暂停采集" : "开始采集";
        var queue = _runtime.Queue;
        _delivery.Text = $"{queue.Pending} 条待交付 · {queue.Failed} 条需处理";
        RefreshConnection();
        _capabilities.Text = string.Join("\n", Enum.GetValues<ObservationCapability>().Select(CapabilityText));
        _message.Text = _actionError ?? _runtime.Error ?? (_performing ? "正在处理…" : string.Empty);
    }

    private void RefreshConnection() =>
        _owner.Text = $"Owner：{_runtime.Settings?.OwnerId.ToString() ?? "尚未接入"}";

    private string CapabilityText(ObservationCapability capability)
    {
        var name = capability switch { ObservationCapability.Application => "前台应用", ObservationCapability.WindowTitle => "窗口标题", _ => "键盘与鼠标" };
        var state = !_runtime.IsCollecting ? "开始采集后检查" : _runtime.Capabilities.FirstOrDefault(item => item.Capability == capability)?.State switch
        { ObservationState.Available => "可用", ObservationState.PermissionRequired => "需要系统授权", ObservationState.Unavailable => "暂不可用", _ => "正在检查" };
        return $"{name}：{state}";
    }

    private void OpenReplay()
    {
        if (_runtime.Settings is not { } settings) return;
        _ = PerformAsync(() =>
        {
            using var process = Process.Start(new ProcessStartInfo(settings.WebUrl.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        });
    }

    private void Show() { if (!_quitting) { AppWindow.Show(); Activate(); } }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _timer.Stop();
        Refresh();
        try { await Task.Run(async () => await _runtime.DisposeAsync()); }
        catch (Exception) { Environment.ExitCode = 1; }
        finally { _tray.Dispose(); Close(); Application.Current.Exit(); }
    }
}
