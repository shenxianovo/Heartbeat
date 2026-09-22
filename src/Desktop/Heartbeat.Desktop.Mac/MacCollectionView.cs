using AppKit;
using Heartbeat.Collector.Desktop;

namespace Heartbeat.Desktop.Mac;

internal sealed class MacCollectionView : NSView
{
    private readonly MacSurface _heroCard;
    private readonly NSImageView _heroDot;
    private readonly NSTextField _statusTitle;
    private readonly NSTextField _statusSubtitle;
    private readonly NSButton _toggle;
    private readonly NSProgressIndicator _progress = MacControls.Progress();
    private readonly Dictionary<ObservationCapability, CapabilityRow> _rows = [];
    private readonly NSTextField _deliveryDetail;
    private readonly StatusPill _deliveryStatus = new();
    private bool _toggling;
    private string? _toggleCaption;
    private string? _heroState;
    private bool _breathing;

    public MacCollectionView(DesktopRuntime runtime, Func<Func<Task>, Task> perform)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _heroDot = MacControls.Symbol("circle.fill", "采集状态", 10);
        _heroDot.WantsLayer = true;
        _statusTitle = MacControls.Label("尚未接入", MacControls.HeadlineFont);
        _statusSubtitle = MacControls.Secondary("");
        _statusSubtitle.MaximumNumberOfLines = 2;
        var title = MacControls.HorizontalStack(8, _heroDot, _statusTitle);
        var text = MacControls.VerticalStack(5, title, _statusSubtitle);
        _toggle = MacControls.Button("开始采集", async () =>
        {
            _toggling = true;
            _toggleCaption = runtime.IsCollecting ? "正在暂停…" : "正在开始…";
            try { await perform(runtime.IsCollecting ? runtime.StopCollectionAsync : runtime.StartAsync); }
            finally
            {
                BeginInvokeOnMainThread(() =>
                {
                    _toggling = false;
                    Refresh(runtime, !runtime.IsBusy, !Hidden && Window?.IsVisible == true);
                });
            }
        }, "play.fill");
        _toggle.WidthAnchor.ConstraintEqualTo(96).Active = true;
        var actions = MacControls.HorizontalStack(8, _progress, _toggle);
        var hero = MacControls.HorizontalStack(16, text, actions);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _heroCard = (MacSurface)MacControls.Card(hero, 12);

        var rows = MacControls.VerticalStack(0);
        AppendCapability(rows, "前台应用", "app", ObservationCapability.Application, null, perform);
        AppendCapability(rows, "窗口标题", "macwindow", ObservationCapability.WindowTitle, "辅助功能设置", perform);
        AppendCapability(rows, "键盘与鼠标", "keyboard", ObservationCapability.Input, "输入监控设置", perform);
        var capabilities = MacControls.Card(rows, 10);
        var privacy = MacControls.Secondary("仅记录物理输入事件，不记录输入的文字。");
        var capabilitiesGroup = MacControls.VerticalStack(8,
            MacControls.Label("采集项目", MacControls.HeadlineFont), capabilities, privacy);
        MacControls.FillWidth(capabilitiesGroup, capabilities, privacy);

        var deliveryTitle = MacControls.Label("同步状态", MacControls.HeadlineFont);
        var spacer = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var deliveryRow = MacControls.HorizontalStack(12, deliveryTitle, spacer, _deliveryStatus);
        _deliveryDetail = MacControls.Secondary("");
        _deliveryDetail.MaximumNumberOfLines = 2;
        var delivery = MacControls.VerticalStack(8, deliveryRow, _deliveryDetail);
        MacControls.FillWidth(delivery, deliveryRow, _deliveryDetail);

        var root = MacControls.VerticalStack(20, _heroCard, capabilitiesGroup, delivery);
        MacControls.FillWidth(root, _heroCard, capabilitiesGroup, delivery);
        AddSubview(root);
        NSLayoutConstraint.ActivateConstraints([
            root.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            root.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            root.TopAnchor.ConstraintEqualTo(TopAnchor),
            root.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        ]);
    }

    private void AppendCapability(NSStackView container, string title, string symbol,
        ObservationCapability capability, string? permissionTitle, Func<Func<Task>, Task> perform)
    {
        if (container.ArrangedSubviews.Length > 0)
        {
            var divider = MacControls.Divider();
            container.AddArrangedSubview(divider);
            MacControls.FillWidth(container, divider);
        }
        var label = MacControls.Label(title, MacControls.BodyFont);
        var spacer = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var status = new StatusPill();
        status.WidthAnchor.ConstraintEqualTo(96).Active = true;
        var permission = MacControls.Button("系统设置…", async () => await perform(() =>
        {
            MacDesktopPlatform.OpenPermissionSettings(capability);
            return Task.CompletedTask;
        }));
        permission.Font = MacControls.FootnoteFont;
        permission.ControlSize = NSControlSize.Small;
        permission.ToolTip = permissionTitle;
        permission.AccessibilityTitle = permissionTitle ?? "无需额外授权";
        permission.WidthAnchor.ConstraintEqualTo(80).Active = true;
        permission.Hidden = permissionTitle is null;
        // Keep all three status columns aligned even without an application action.
        var slot = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        slot.WidthAnchor.ConstraintEqualTo(80).Active = true;
        slot.HeightAnchor.ConstraintEqualTo(28).Active = true;
        slot.AddSubview(permission);
        permission.CenterXAnchor.ConstraintEqualTo(slot.CenterXAnchor).Active = true;
        permission.CenterYAnchor.ConstraintEqualTo(slot.CenterYAnchor).Active = true;
        var row = MacControls.HorizontalStack(10, MacControls.Symbol(symbol, title), label, spacer, status, slot);
        row.HeightAnchor.ConstraintEqualTo(36).Active = true;
        container.AddArrangedSubview(row);
        MacControls.FillWidth(container, row);
        _rows[capability] = new CapabilityRow(status, permission);
    }

    public void Refresh(DesktopRuntime runtime, bool enabled, bool animateDecorations)
    {
        UpdateHero(runtime);
        UpdateAction(runtime, enabled);
        UpdateBreathing(runtime.IsCollecting && animateDecorations);
        UpdateCapabilities(runtime, enabled);
        UpdateDelivery(runtime);
    }

    private void UpdateAction(DesktopRuntime runtime, bool enabled)
    {
        _toggle.Enabled = enabled && runtime.Settings is not null && !_toggling;
        _toggle.Title = _toggling ? _toggleCaption! : runtime.IsCollecting ? "暂停采集" : "开始采集";
        _toggle.Image = NSImage.GetSystemSymbol(runtime.IsCollecting ? "pause.fill" : "play.fill", _toggle.Title);
        if (_toggling && !Hidden) _progress.StartAnimation(null);
        else _progress.StopAnimation(null);
    }

    private void UpdateBreathing(bool visibleAndCollecting)
    {
        var breathe = visibleAndCollecting && !MacAnimation.ReduceMotion;
        if (_breathing != breathe)
        {
            MacAnimation.SetBreathing(_heroDot.Layer!, breathe);
            _breathing = breathe;
        }
    }

    private void UpdateCapabilities(DesktopRuntime runtime, bool enabled)
    {
        foreach (var (capability, row) in _rows)
        {
            var state = runtime.IsCollecting
                ? runtime.Capabilities.FirstOrDefault(item => item.Capability == capability)?.State
                : null;
            UpdateCapability(row.Status, state, runtime.IsCollecting);
            row.Permission.Enabled = enabled;
        }
    }

    private void UpdateHero(DesktopRuntime runtime)
    {
        var state = runtime.Settings is null ? "unconfigured" : runtime.IsCollecting ? "collecting" : "paused";
        if (_heroState == state) return;
        _heroState = state;
        MacAnimation.FadeContentChange(_heroCard, () =>
        {
            var collecting = state == "collecting";
            _heroDot.ContentTintColor = collecting ? NSColor.SystemGreen : NSColor.SecondaryLabel;
            _statusTitle.StringValue = state switch
            {
                "collecting" => "正在采集",
                "paused" => "采集已暂停",
                _ => "尚未接入",
            };
            _statusSubtitle.StringValue = state switch
            {
                "collecting" => "仅记录当前可用的采集项目",
                "paused" => "已保存在本机的数据仍会继续上传",
                _ => "先完成连接设置，即可开始采集",
            };
        });
    }

    private void UpdateDelivery(DesktopRuntime runtime)
    {
        if (runtime.Settings is null)
        {
            _deliveryStatus.Update("待连接", NSColor.SecondaryLabel, "cloud");
            SetDeliveryDetail("完成连接设置后，采集数据会先保存在本机，再上传。");
            return;
        }
        var queue = runtime.Queue;
        if (queue.Failed > 0)
            _deliveryStatus.Update($"{queue.Pending} 条待上传 · {queue.Failed} 条需处理", NSColor.SystemOrange, "exclamationmark.icloud");
        else if (queue.Pending > 0)
            _deliveryStatus.Update($"{queue.Pending} 条待上传", NSColor.SecondaryLabel, "arrow.triangle.2.circlepath");
        else
            _deliveryStatus.Update("已同步", NSColor.SystemGreen, "checkmark.circle.fill");
        SetDeliveryDetail(queue.Failed > 0
            ? "上传遇到问题，会自动重试。暂停采集不影响上传。"
            : "数据已保存在本机，连接恢复后会继续上传。");
    }

    private void SetDeliveryDetail(string text)
    {
        if (_deliveryDetail.StringValue == text) return;
        MacAnimation.FadeContentChange(_deliveryDetail, () => _deliveryDetail.StringValue = text);
    }

    private static void UpdateCapability(StatusPill status, ObservationState? state, bool collecting)
    {
        var (text, color, symbol) = collecting ? state switch
        {
            ObservationState.Available => ("可用", NSColor.SystemGreen, "checkmark.circle.fill"),
            ObservationState.PermissionRequired => ("需要系统授权", NSColor.SystemOrange, "exclamationmark.circle"),
            ObservationState.Unavailable => ("暂不可用", NSColor.SystemRed, "xmark.circle"),
            _ => ("正在检查", NSColor.SecondaryLabel, "hourglass"),
        } : ("开始后检查", NSColor.SecondaryLabel, "pause.circle");
        status.Update(text, color, symbol);
    }

    private sealed record CapabilityRow(StatusPill Status, NSButton Permission);
}
