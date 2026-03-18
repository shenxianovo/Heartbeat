using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Http;
using Heartbeat.Agent.Services;
using Heartbeat.Agent.Storage;
using Heartbeat.Agent.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Agent.Hosting
{
    public static class AgentHostExtensions
    {
        /// <summary>
        /// 注册 Heartbeat Agent 的所有服务和后台任务
        /// </summary>
        public static IServiceCollection AddHeartbeatAgent(
            this IServiceCollection services,
            ConfigManager? configManager = null)
        {
            // ConfigManager（外部可注入已有实例，如 WPF 已创建）
            if (configManager != null)
            {
                services.AddSingleton(configManager);
            }
            else
            {
                services.AddSingleton<ConfigManager>();
            }

            // HttpClient（命名客户端 + ApiKey 处理器）
            services.AddTransient<ApiKeyDelegatingHandler>();
            services.AddHttpClient("HeartbeatApi")
                .AddHttpMessageHandler<ApiKeyDelegatingHandler>();

            // 本地缓存
            services.AddSingleton(sp =>
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var cachePath = Path.Combine(localAppData, "Heartbeat", "cache.json");
                return new LocalCache(cachePath);
            });

            // 业务服务
            services.AddSingleton<AppMonitorService>();
            services.AddSingleton<UsageUploadService>();
            services.AddSingleton<IconUploadService>();
            services.AddSingleton<StatusUploadService>();

            // 自启动服务
            services.AddSingleton<IAutoStartService, RegistryAutoStartService>();

            // 托管后台服务
            services.AddHostedService(sp => sp.GetRequiredService<AppMonitorService>());
            services.AddHostedService<UsageUploadWorker>();
            services.AddHostedService<StatusUploadWorker>();

            return services;
        }
    }
}
