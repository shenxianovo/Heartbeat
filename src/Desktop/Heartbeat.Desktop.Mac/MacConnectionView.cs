using AppKit;
using CoreGraphics;

namespace Heartbeat.Desktop.Mac;

internal sealed class MacConnectionView : NSView
{
    private readonly NSTextField _backend;
    private readonly NSTextField _auth;
    private readonly NSTextField _web;
    private readonly NSTextField _key;
    private readonly NSTextField _owner;
    private readonly NSButton _save;

    public MacConnectionView(DesktopRuntime runtime, Func<Func<Task>, Task> perform) : base(new CGRect(0, 0, 680, 460))
    {
        _backend = MacControls.Field(this, "后端地址", runtime.Settings?.BackendUrl.AbsoluteUri ?? "http://localhost:8080", 362);
        _auth = MacControls.Field(this, "Auth 地址", runtime.Settings?.AuthUrl.AbsoluteUri ?? "https://auth.shenxianovo.com", 285);
        _web = MacControls.Field(this, "Web 时间线地址", runtime.Settings?.WebUrl.AbsoluteUri ?? "http://localhost:3000", 208);
        _key = MacControls.Field(this, "API key", string.Empty, 131, secret: true);
        _key.PlaceholderString = "留空使用已保存的凭据";
        MacControls.Label(this, "API key 保存到系统钥匙串。首次接入需要联网验证账号。", 24, 90, 620);
        _owner = MacControls.Label(this, string.Empty, 24, 24, 450, 48);
        _save = MacControls.Button(this, "验证并保存", 504, 28, 140, async () =>
        {
            // Read AppKit controls on the main thread before entering background work.
            var backend = _backend.StringValue;
            var auth = _auth.StringValue;
            var web = _web.StringValue;
            var key = _key.StringValue;
            await perform(async () =>
            {
                await runtime.ConfigureAsync(new Uri(backend), new Uri(auth), new Uri(web), key);
                BeginInvokeOnMainThread(() => _key.StringValue = string.Empty);
            });
        });
    }

    public void Refresh(DesktopSettings? settings, bool enabled)
    {
        _owner.StringValue = $"Owner\n{settings?.OwnerId.ToString() ?? "尚未接入"}";
        _save.Enabled = enabled;
        _backend.Enabled = _auth.Enabled = _web.Enabled = _key.Enabled = enabled;
    }
}
