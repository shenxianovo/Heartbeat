using Heartbeat.Agent.Utils;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Hub.Core.Http;
using Serilog;

namespace Heartbeat.Agent.Services;

public interface IIconUploadService
{
    Task EnsureIconUploadedAsync(string appIdentityKey, string? appDisplayName);
}

public interface IAppIconExtractor
{
    byte[]? Extract(string appIdentityKey);
}

public sealed class WindowsAppIconExtractor : IAppIconExtractor
{
    public byte[]? Extract(string appIdentityKey)
    {
        var normalized = AppIdentityKeys.Normalize(appIdentityKey);
        if (!normalized.StartsWith(AppIdentityKeys.WindowsPrefix, StringComparison.Ordinal))
            return null;
        return IconHelper.GetIconPngByProcessName(normalized[AppIdentityKeys.WindowsPrefix.Length..]);
    }
}

public sealed class IconUploadService(
    HeartbeatApiClient apiClient,
    IAppIconExtractor extractor,
    ClientCompatibilityStatus compatibilityStatus) : IIconUploadService
{
    private readonly HashSet<string> _uploadedIdentities = new(StringComparer.OrdinalIgnoreCase);

    public async Task EnsureIconUploadedAsync(string appIdentityKey, string? appDisplayName)
    {
        if (compatibilityStatus.Current.UpdateRequired) return;

        var normalized = AppIdentityKeys.Normalize(appIdentityKey);
        if (_uploadedIdentities.Contains(normalized)) return;

        var iconData = extractor.Extract(normalized);
        if (iconData is not { Length: > 0 })
        {
            Log.Warning("无法提取图标，跳过上传: {AppIdentity}", normalized);
            return;
        }

        var result = await apiClient.UploadAppIconAsync(new IconUploadRequest
        {
            AppIdentityKey = normalized,
            AppDisplayName = appDisplayName,
            IconData = iconData
        });
        if (result.StatusCode == 426)
        {
            compatibilityStatus.RequireUpdate(result.ResponseBody);
            return;
        }
        if (!result.Success) return;

        _uploadedIdentities.Add(normalized);
        Log.Information("图标上传成功: {AppIdentity}", normalized);
    }
}
