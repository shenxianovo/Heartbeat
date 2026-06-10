using Heartbeat.Agent.Http;
using Heartbeat.Agent.Utils;
using Heartbeat.Core.DTOs.Apps;
using Serilog;

namespace Heartbeat.Agent.Services
{
    public class IconUploadService(HeartbeatApiClient apiClient)
    {
        private readonly HashSet<string> _uploadedApps = new(StringComparer.OrdinalIgnoreCase);

        public async Task EnsureIconUploadedAsync(string appName)
        {
            if (_uploadedApps.Contains(appName))
                return;

            Log.Debug("检查图标: {App}", appName);

            var iconData = IconHelper.GetIconPngByProcessName(appName);
            if (iconData == null || iconData.Length == 0)
            {
                Log.Warning("无法提取图标，跳过上传: {App}", appName);
                return;
            }

            Log.Debug("正在上传图标: {App}，大小 {Size} bytes", appName, iconData.Length);
            var request = new IconUploadRequest
            {
                AppName = appName,
                IconData = iconData
            };

            var result = await apiClient.UploadAppIconAsync(request);
            if (result.Success)
            {
                _uploadedApps.Add(appName);
                Log.Information("图标上传成功: {App}", appName);
            }
        }
    }
}
