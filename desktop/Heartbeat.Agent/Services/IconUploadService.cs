using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Utils;
using Heartbeat.Core.DTOs.Apps;
using Serilog;
using System.Net.Http.Json;

namespace Heartbeat.Agent.Services
{
    public class IconUploadService(ConfigManager configManager, IHttpClientFactory httpClientFactory)
    {
        private readonly HashSet<string> _uploadedApps = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 检查并上传应用图标（幂等，已上传过的不会重复上传）
        /// </summary>
        public async Task EnsureIconUploadedAsync(string appName)
        {
            if (_uploadedApps.Contains(appName))
                return;

            Log.Debug("检查图标: {App}", appName);

            try
            {
                var iconData = IconHelper.GetIconPngByProcessName(appName);
                if (iconData == null || iconData.Length == 0)
                {
                    Log.Warning("无法提取图标，跳过上传: {App}", appName);
                    return;
                }

                var config = configManager.Current;
                var appsUrl = $"{config.ApiBaseUrl}/apps";

                Log.Debug("正在上传图标: {App}，大小 {Size} bytes", appName, iconData.Length);
                var request = new IconUploadRequest
                {
                    AppName = appName,
                    IconData = iconData
                };

                var client = httpClientFactory.CreateClient("HeartbeatApi");
                var res = await client.PostAsJsonAsync($"{appsUrl}/icon", request);
                if (res.IsSuccessStatusCode)
                {
                    _uploadedApps.Add(appName);
                    Log.Information("图标上传成功: {App}", appName);
                }
                else
                {
                    var body = await res.Content.ReadAsStringAsync();
                    Log.Warning("图标上传失败 [{StatusCode}]: {App}，响应: {Body}", (int)res.StatusCode, appName, body);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "图标上传异常: {App}", appName);
            }
        }
    }
}
