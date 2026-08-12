using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Hub.Core.Http;
using Heartbeat.Hub.Core.Runtime;
using Serilog;

namespace Heartbeat.Agent.Mac.Icons;

public sealed class MacIconUploadService(
    HeartbeatApiClient api,
    MacBundleIconExtractor extractor,
    ClientCompatibilityStatus compatibility)
{
    private readonly HashSet<string> _uploaded = new(StringComparer.OrdinalIgnoreCase);

    public async Task EnsureUploadedAsync(string appIdentityKey, string? appDisplayName)
    {
        if (compatibility.Current.UpdateRequired) return;
        var normalized = AppIdentityKeys.Normalize(appIdentityKey);
        if (_uploaded.Contains(normalized)) return;
        var icon = extractor.Extract(normalized);
        if (icon is not { Length: > 0 }) return;

        var result = await api.UploadAppIconAsync(new IconUploadRequest
        {
            AppIdentityKey = normalized,
            AppDisplayName = appDisplayName,
            IconData = icon,
        });
        if (result.StatusCode == 426)
        {
            compatibility.RequireUpdate(result.ResponseBody);
            return;
        }
        if (result.Success)
            _uploaded.Add(normalized);
    }
}

public sealed class MacHubRuntimeHooks(MacIconUploadService icons) : IHubRuntimeHooks
{
    public void OnStarting() { }

    public async Task SegmentsDrainedAsync(IReadOnlyCollection<ActivitySegmentItem> segments)
    {
        foreach (var app in segments
                     .Where(segment => !string.IsNullOrWhiteSpace(segment.AppIdentityKey))
                     .Select(segment => (Identity: segment.AppIdentityKey!, segment.AppDisplayName))
                     .DistinctBy(item => item.Identity, StringComparer.OrdinalIgnoreCase))
        {
            try { await icons.EnsureUploadedAsync(app.Identity, app.AppDisplayName); }
            catch (Exception exception) { Log.Warning(exception, "macOS AppIcon 上传失败: {Identity}", app.Identity); }
        }
    }
}
