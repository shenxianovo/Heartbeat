using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Http;
using Heartbeat.Agent.Services;
using Heartbeat.Agent.Storage;
using Heartbeat.Agent.Utils;
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
            ConfigManager? configManager = null,
            SingleInstanceGuard? guard = null)
        {
            // 单实例守卫（由调用方创建并传入，Agent 负责管理生命周期）
            if (guard != null)
            {
                services.AddSingleton(guard);
            }

            // ConfigManager（外部可注入已有实例，如 WPF 已创建）
            if (configManager != null)
            {
                services.AddSingleton(configManager);
            }
            else
            {
                services.AddSingleton<ConfigManager>();
            }

            // TokenManager（缓存 AuthService JWT）
            services.AddSingleton<TokenManager>();
            services.AddSingleton<IAccessTokenProvider>(sp => sp.GetRequiredService<TokenManager>());

            // Typed HttpClient for AuthService token exchange (plain, no auth handler)
            services.AddHttpClient<AuthServiceClient>();

            // Typed HttpClient for Heartbeat API（注入 Bearer 处理器）
            services.AddTransient<BearerTokenHandler>();
            services.AddHttpClient<HeartbeatApiClient>()
                .AddHttpMessageHandler<BearerTokenHandler>();

            // 本地缓存
            services.AddSingleton<IInputEventCache>(sp =>
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var cachePath = Path.Combine(localAppData, "Heartbeat", "input-events-cache.json");
                return new InputEventLocalCache(cachePath);
            });

            services.AddSingleton<ISegmentCache>(sp =>
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var cachePath = Path.Combine(localAppData, "Heartbeat", "segments-cache.json");
                return new SegmentLocalCache(cachePath);
            });

            // 基础设施
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<IWindowEventMonitor, WindowsWindowEventMonitor>();
            services.AddSingleton<ILowLevelInputHook, WindowsLowLevelInputHook>();
            services.AddSingleton<IPowerMonitor, WindowsPowerMonitor>();
            services.AddSingleton<IInputActivitySignal, InputActivitySignal>();

            // 业务服务
            services.AddSingleton<AppMonitorService>();
            services.AddSingleton<IconUploadService>();
            services.AddSingleton<StatusUploadService>();
            services.AddSingleton<InputEventCollector>();
            services.AddSingleton<InputEventUploadService>();
            services.AddSingleton<SegmentIngestService>();
            services.AddSingleton<ISegmentSink>(sp => sp.GetRequiredService<SegmentIngestService>());
            services.AddSingleton<SegmentIngestRequestHandler>();
            services.AddSingleton<SegmentUploadService>();

            // 自启动服务
            services.AddSingleton<IAutoStartService, RegistryAutoStartService>();

            // 托管后台服务。停止顺序为注册的逆序：AppMonitorService 必须最后注册、最先停止，
            // 使其终态快照先推入 hub，再由 UsageUploadWorker.StopAsync 的最终 drain 带走（ADR-020）。
            services.AddHostedService(sp => sp.GetRequiredService<InputEventCollector>());
            services.AddHostedService<UsageUploadWorker>();
            services.AddHostedService<StatusUploadWorker>();
            services.AddHostedService<SegmentIngestWorker>();
            services.AddHostedService(sp => sp.GetRequiredService<AppMonitorService>());

            return services;
        }
    }
}
