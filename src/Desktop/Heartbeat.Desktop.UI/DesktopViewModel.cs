using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Heartbeat.Collector.Desktop;

namespace Heartbeat.Desktop.UI;

public sealed class DesktopViewModel(DesktopRuntime runtime) : INotifyPropertyChanged
{
    private bool _busy;
    private string? _message;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Backend { get; set; } = runtime.Settings?.BackendUrl.AbsoluteUri ?? "http://localhost:8080";
    public string Auth { get; set; } = runtime.Settings?.AuthUrl.AbsoluteUri ?? "https://auth.shenxianovo.com";
    public string Web { get; set; } = runtime.Settings?.WebUrl.AbsoluteUri ?? "http://localhost:3000";
    public string ApiKey { get; set; } = string.Empty;
    public bool IsBusy => _busy;
    public bool CanInteract => !_busy;
    public bool IsConfigured => runtime.Settings is not null;
    public string CollectorName => runtime.CollectorName;
    public string Status => runtime.IsCollecting ? "正在采集" : "采集已暂停";
    public string ToggleText => runtime.IsCollecting ? "暂停采集" : "开始采集";
    public string Owner => runtime.Settings?.OwnerId.ToString() ?? "尚未接入";
    public string Delivery => !IsConfigured ? "保存连接后开始" : $"{runtime.Queue.Pending} 条待交付 · {runtime.Queue.Failed} 条需处理";
    public string? Message => _message ?? runtime.Error;
    public string ApplicationPermission => CapabilityText(ObservationCapability.Application);
    public string WindowPermission => CapabilityText(ObservationCapability.WindowTitle);
    public string InputPermission => CapabilityText(ObservationCapability.Input);

    public Task SaveAsync() => PerformAsync(async () =>
    {
        await runtime.ConfigureAsync(new Uri(Backend), new Uri(Auth), new Uri(Web), ApiKey);
        ApiKey = string.Empty;
        Changed(nameof(ApiKey));
        _message = "连接已保存，API key 已交给系统凭据库。可以开始采集。";
    });

    public Task ToggleAsync() => PerformAsync(async () =>
    {
        if (runtime.IsCollecting) await runtime.StopCollectionAsync();
        else await runtime.StartAsync();
    });

    public Task OpenReplayAsync() => PerformAsync(() =>
    {
        var url = runtime.Settings?.WebUrl ?? throw new InvalidOperationException("请先保存连接设置。");
        using var process = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    });

    public Task OpenPermissionAsync(ObservationCapability capability) => PerformAsync(() =>
    {
        runtime.OpenPermissionSettings(capability);
        return Task.CompletedTask;
    });

    private async Task PerformAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _message = null;
        Refresh();
        try { await action(); }
        catch (Exception exception) { _message = exception.Message; }
        finally { _busy = false; Refresh(); }
    }

    private string CapabilityText(ObservationCapability capability)
    {
        if (!runtime.IsCollecting) return "开始采集后检查";
        return runtime.Capabilities.FirstOrDefault(item => item.Capability == capability)?.State switch
        {
            ObservationState.Available => "可用",
            ObservationState.PermissionRequired => "需要系统授权",
            ObservationState.Unavailable => "暂不可用",
            _ => "正在检查",
        };
    }

    public void Refresh() => Changed(string.Empty);
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
