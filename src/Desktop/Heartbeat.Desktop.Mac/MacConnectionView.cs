using AppKit;

namespace Heartbeat.Desktop.Mac;

internal sealed class MacConnectionView : NSView
{
    private readonly NSTextField _backend;
    private readonly NSTextField _auth;
    private readonly NSTextField _web;
    private readonly NSTextField _key;
    private readonly NSButton _save;
    private readonly NSProgressIndicator _progress = MacControls.Progress();
    private readonly NSTextField _ownerLabel;
    private readonly StatusPill _ownerStatus = new();
    private bool _saving;
    public NSView InitialFocus => _backend;

    public MacConnectionView(DesktopRuntime runtime, Func<Func<Task>, Task> perform)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _backend = MacControls.Field("https://example.com");
        _backend.StringValue = runtime.Settings?.BackendUrl.AbsoluteUri ?? "http://localhost:8080";
        _auth = MacControls.Field("https://auth.example.com");
        _auth.StringValue = runtime.Settings?.AuthUrl.AbsoluteUri ?? "https://auth.shenxianovo.com";
        _web = MacControls.Field("https://timeline.example.com");
        _web.StringValue = runtime.Settings?.WebUrl.AbsoluteUri ?? "http://localhost:3000";
        _key = MacControls.Field("留空使用已保存的凭据", true);
        var fields = MacControls.VerticalStack(12);
        foreach (var (title, field) in new[] { ("后端地址", _backend), ("Auth 地址", _auth),
            ("Web 时间线地址", _web), ("API key", _key) })
        {
            field.AccessibilityLabel = title;
            var row = BuildRow(title, field);
            fields.AddArrangedSubview(row);
            MacControls.FillWidth(fields, row);
        }

        _save = MacControls.Button("验证并保存", async () =>
        {
            var backend = _backend.StringValue;
            var auth = _auth.StringValue;
            var web = _web.StringValue;
            var key = _key.StringValue;
            _saving = true;
            try
            {
                await perform(async () =>
                {
                    await runtime.ConfigureAsync(new Uri(backend), new Uri(auth), new Uri(web), key);
                    BeginInvokeOnMainThread(() => _key.StringValue = string.Empty);
                });
            }
            finally
            {
                BeginInvokeOnMainThread(() =>
                {
                    _saving = false;
                    Refresh(runtime.Settings, !runtime.IsBusy);
                });
            }
        }, "checkmark.shield", true);
        _save.WidthAnchor.ConstraintEqualTo(122).Active = true;
        var spacer = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var saveRow = MacControls.HorizontalStack(8, spacer, _progress, _save);
        var hint = MacControls.Secondary(MacCredentialStorage.SaveHint);
        var form = MacControls.VerticalStack(18, fields, hint, saveRow);
        MacControls.FillWidth(form, fields, hint, saveRow);
        var formCard = MacControls.Card(form, 18);

        _ownerLabel = MacControls.Label("尚未接入", MacControls.FootnoteFont, NSColor.SecondaryLabel);
        _ownerLabel.Selectable = true;
        _ownerLabel.MaximumNumberOfLines = 2;
        _ownerLabel.AccessibilityLabel = "Owner ID";
        var ownerHeader = MacControls.HorizontalStack(12,
            MacControls.Label("当前账号", MacControls.HeadlineFont),
            new NSView { TranslatesAutoresizingMaskIntoConstraints = false }, _ownerStatus);
        var ownerText = MacControls.VerticalStack(4, MacControls.Secondary("Owner ID"), _ownerLabel);
        var ownerContent = MacControls.VerticalStack(12, ownerHeader, ownerText);
        MacControls.FillWidth(ownerContent, ownerHeader, ownerText);
        MacControls.FillWidth(ownerText, _ownerLabel);
        var ownerCard = MacControls.Card(ownerContent);
        var root = MacControls.VerticalStack(16, formCard, ownerCard);
        MacControls.FillWidth(root, formCard, ownerCard);
        AddSubview(root);
        NSLayoutConstraint.ActivateConstraints([
            root.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            root.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            root.TopAnchor.ConstraintEqualTo(TopAnchor),
            root.BottomAnchor.ConstraintEqualTo(BottomAnchor),
        ]);
    }

    private static NSStackView BuildRow(string title, NSTextField field)
    {
        var label = MacControls.Label(title, MacControls.BodyFont, NSColor.SecondaryLabel);
        label.Alignment = NSTextAlignment.Right;
        label.WidthAnchor.ConstraintEqualTo(100).Active = true;
        return MacControls.HorizontalStack(14, label, field);
    }

    public void Refresh(DesktopSettings? settings, bool enabled)
    {
        _ownerLabel.StringValue = settings?.OwnerId.ToString() ?? "尚未接入";
        _ownerStatus.Update(settings is null ? "待连接" : "已验证",
            settings is null ? NSColor.SecondaryLabel : NSColor.SystemGreen,
            settings is null ? "circle" : "checkmark.circle.fill");
        _save.Title = _saving ? "正在验证…" : "验证并保存";
        if (_saving && !Hidden) _progress.StartAnimation(null);
        else _progress.StopAnimation(null);
        _save.Enabled = enabled && !_saving;
        _backend.Enabled = _auth.Enabled = _web.Enabled = _key.Enabled = enabled;
    }
}
