using AppKit;
using CoreGraphics;
using Heartbeat.Collector.Desktop;

namespace Heartbeat.Desktop.Mac;

internal sealed class MacCollectionView : NSView
{
    private readonly NSTextField _status;
    private readonly NSTextField _delivery;
    private readonly Dictionary<ObservationCapability, NSTextField> _capabilities = [];
    private readonly List<NSButton> _buttons = [];
    private readonly NSButton _toggle;

    public MacCollectionView(DesktopRuntime runtime, Func<Func<Task>, Task> perform)
        : base(new CGRect(0, 0, 680, 460))
    {
        _status = MacControls.Label(this, string.Empty, 24, 390, 450, 32);
        _status.Font = NSFont.BoldSystemFontOfSize(22)!;
        MacControls.Label(this, "关闭窗口后继续运行，可从菜单栏重新打开。", 24, 355, 620);
        _toggle = MacControls.Button(this, "开始采集", 504, 390, 140, async () =>
        {
            var collecting = runtime.IsCollecting;
            await perform(collecting ? runtime.StopCollectionAsync : runtime.StartAsync);
        });
        _buttons.Add(_toggle);
        AddCapability("前台应用", ObservationCapability.Application, 282);
        AddCapability("窗口标题", ObservationCapability.WindowTitle, 234);
        AddCapability("键盘与鼠标", ObservationCapability.Input, 186);
        AddPermission("辅助功能设置", ObservationCapability.WindowTitle, 232, perform);
        AddPermission("输入监控设置", ObservationCapability.Input, 184, perform);
        MacControls.Label(this, "只记录非文本物理输入事件，不记录输入的文字。授权后会自动重新检查。", 24, 125, 620, 44);
        _delivery = MacControls.Label(this, string.Empty, 24, 76, 620);
        MacControls.Label(this, "已接管的数据保存在本地，连接恢复后继续上传。暂停采集不影响交付。", 24, 20, 620, 44);
    }

    private void AddCapability(string title, ObservationCapability capability, double y)
    {
        MacControls.Label(this, title, 24, y, 230);
        _capabilities[capability] = MacControls.Label(this, string.Empty, 260, y, 230);
    }

    private void AddPermission(string title, ObservationCapability capability, double y, Func<Func<Task>, Task> perform) =>
        _buttons.Add(MacControls.Button(this, title, 504, y, 140, async () => await perform(() =>
        {
            MacDesktopPlatform.OpenPermissionSettings(capability);
            return Task.CompletedTask;
        })));


    public void Refresh(DesktopRuntime runtime, bool enabled)
    {
        _status.StringValue = runtime.IsCollecting ? "正在采集" : "采集已暂停";
        _toggle.Title = runtime.IsCollecting ? "暂停采集" : "开始采集";
        var queue = runtime.Queue;
        _delivery.StringValue = runtime.Settings is null ? "保存连接后开始" : $"{queue.Pending} 条待交付 · {queue.Failed} 条需处理";
        foreach (var button in _buttons) button.Enabled = enabled;
        foreach (var (capability, label) in _capabilities)
            label.StringValue = CapabilityText(runtime, capability);
    }

    private static string CapabilityText(DesktopRuntime runtime, ObservationCapability capability) =>
        !runtime.IsCollecting ? "开始采集后检查" : runtime.Capabilities.FirstOrDefault(item => item.Capability == capability)?.State switch
        {
            ObservationState.Available => "可用",
            ObservationState.PermissionRequired => "需要系统授权",
            ObservationState.Unavailable => "暂不可用",
            _ => "正在检查",
        };
}
