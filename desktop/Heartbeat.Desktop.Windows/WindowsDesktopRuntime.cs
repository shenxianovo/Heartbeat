using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Utils;
using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.Views;
using Heartbeat.Hub.Core.Http;
using Heartbeat.Hub.Core.Presence;
using Heartbeat.Hub.Core.Upload;
using Heartbeat.Agent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Desktop.Windows;

public sealed class WindowsDesktopRuntime : IWindowController, IAsyncDisposable
{
    private readonly IHost _host;
    private readonly SingleInstanceGuard _guard;
    private int _stopped;
    private int _quitting;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private MainWindow? _window;
    private TrayIcon? _trayIcon;

    public WindowsDesktopRuntime(
        IHost host,
        SingleInstanceGuard guard,
        ConfigManager config,
        RingBufferSink logFeed)
    {
        _host = host;
        _guard = guard;
        LogFeed = logFeed;
        DesktopState = new WindowsDesktopState(
            config,
            host.Services.GetRequiredService<ICollectionStatus>(),
            host.Services.GetRequiredService<IAutoStartService>(),
            host.Services.GetRequiredService<IClientCompatibilityStatus>(),
            host.Services.GetRequiredService<IUploadStatus>());
        Updates = new WindowsUpdateController(PrepareForUpdateAsync);
        Updates.Start();
    }

    public WindowsDesktopState DesktopState { get; }
    public WindowsUpdateController Updates { get; }
    public RingBufferSink LogFeed { get; }
    public bool IsShutdownPrepared => Volatile.Read(ref _stopped) != 0;

    public void Attach(
        IClassicDesktopStyleApplicationLifetime lifetime,
        MainWindow window,
        TrayIcon trayIcon)
    {
        _lifetime = lifetime;
        _window = window;
        _trayIcon = trayIcon;
    }

    public void ShowSettings() => Dispatcher.UIThread.Post(() =>
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    });

    public void HideSettings() => Dispatcher.UIThread.Post(() => _window?.Hide());

    public async Task QuitAsync()
    {
        if (Interlocked.Exchange(ref _quitting, 1) != 0) return;
        await StopAgentAsync();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_window != null) _window.AllowClose = true;
            _trayIcon?.Dispose();
        });
        _guard.Dispose();

        if (!Updates.ApplyOnExitIfReady())
            await Dispatcher.UIThread.InvokeAsync(() => _lifetime?.Shutdown());
    }

    private async Task PrepareForUpdateAsync()
    {
        await StopAgentAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_window != null) _window.AllowClose = true;
            _trayIcon?.Dispose();
        });
        _guard.Dispose();
    }

    private async Task StopAgentAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        Log.Information("正在停止 Heartbeat Agent...");
        await _host.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAgentAsync();
        Updates.Dispose();
        DesktopState.Dispose();
        _trayIcon?.Dispose();
    }
}
