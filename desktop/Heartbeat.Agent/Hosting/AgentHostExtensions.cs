using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Services;
using Heartbeat.Agent.Utils;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Desktop.Core.Collection;
using Heartbeat.Desktop.Core.Configuration;
using Heartbeat.Desktop.Core.Input;
using Heartbeat.Desktop.Core.Observations;
using Heartbeat.Hub.Core.Presence;
using Heartbeat.Hub.Core.Segments;
using Heartbeat.Hub.Core.Storage;
using Heartbeat.Hub.Core.Time;
using Heartbeat.Hub.Core.Upload;
using Heartbeat.Hub.Core.Hosting;
using Heartbeat.Hub.Core.Configuration;
using Heartbeat.Hub.Core.Collectors;
using Heartbeat.Hub.Core.Runtime;
using Heartbeat.Hub.Core.Http;
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
            // 纯 .NET hub 运行时；无头 host 可独立调用同一入口，不会带入 desktop/UI。
            services.AddHeartbeatHubCore();

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

            services.AddSingleton<HubConfigurationAdapter>();
            services.AddSingleton<IHubConfiguration>(sp => sp.GetRequiredService<HubConfigurationAdapter>());
            services.AddSingleton<ICollectorRegistry>(sp => sp.GetRequiredService<HubConfigurationAdapter>());

            // 本地缓存（JsonFileCache 直接充当 ICache<T> 生产 adapter，ADR-020）
            services.AddSingleton<ICache<InputEventItem>>(sp =>
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var cachePath = Path.Combine(localAppData, "Heartbeat", "input-events-cache.json");
                return new JsonFileCache<InputEventItem>(
                    cachePath,
                    maxItems: 100_000,
                    HeartbeatCacheFormats.InputEventVersion1(),
                    HeartbeatCacheFormats.InputEventMigrations());
            });

            services.AddSingleton<ICache<ActivitySegmentItem>>(sp =>
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var cachePath = Path.Combine(localAppData, "Heartbeat", "segments-cache.json");
                return new JsonFileCache<ActivitySegmentItem>(
                    cachePath,
                    maxItems: 20_000,
                    HeartbeatCacheFormats.SegmentVersion2(),
                    HeartbeatCacheFormats.SegmentMigrations());
            });

            // 基础设施
            services.AddSingleton<IMachineIdentity, WindowsMachineIdentity>();
            services.AddSingleton<IDeviceIdentity, WindowsDeviceIdentity>();
            services.AddSingleton<IHubRuntimeHooks, WindowsHubRuntimeHooks>();
            services.AddSingleton<IWindowEventMonitor, WindowsWindowEventMonitor>();
            services.AddSingleton<ILowLevelInputHook, WindowsLowLevelInputHook>();
            services.AddSingleton<IPowerMonitor, WindowsPowerMonitor>();
            services.AddSingleton<IDesktopObservationSource, WindowsDesktopObservationSource>();
            services.AddSingleton<IDesktopSettings, DesktopSettingsAdapter>();
            services.AddSingleton<IInputActivitySignal, InputActivitySignal>();

            // 业务服务
            services.AddSingleton<IAppIconExtractor, WindowsAppIconExtractor>();
            services.AddSingleton<IconUploadService>();
            services.AddSingleton<IIconUploadService>(sp => sp.GetRequiredService<IconUploadService>());
            // 输入缓冲为共享单例：collector 写入，出网侧经 IUploadSource drain
            services.AddSingleton(sp => new InputEventBuffer(sp.GetRequiredService<IClock>()));
            services.AddSingleton<IUploadSource<InputEventItem>>(sp => sp.GetRequiredService<InputEventBuffer>());
            // 上传流（ADR-020/022）：绑定源 + 出网 + 缓存；行为差异只剩注入的 compact 策略
            services.AddSingleton(sp =>
            {
                var api = sp.GetRequiredService<HeartbeatApiClient>();
                return new UploadStream<ActivitySegmentItem>(
                    "段",
                    sp.GetRequiredService<IUploadSource<ActivitySegmentItem>>(),
                    batch => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
                    sp.GetRequiredService<ICache<ActivitySegmentItem>>(),
                    SnapshotCompaction.KeepLatest,
                    new JsonDeadLetterStore<ActivitySegmentItem>(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Heartbeat",
                        "segments-dead-letter.json")),
                    sp.GetRequiredService<UploadStatusRegistry>(),
                    sp.GetRequiredService<ClientCompatibilityStatus>());
            });
            services.AddSingleton(sp =>
            {
                var api = sp.GetRequiredService<HeartbeatApiClient>();
                return new UploadStream<InputEventItem>(
                    "输入事件",
                    sp.GetRequiredService<IUploadSource<InputEventItem>>(),
                    batch => api.UploadInputEventsAsync(new InputEventUploadRequest { Events = batch }),
                    sp.GetRequiredService<ICache<InputEventItem>>(),
                    deadLetterStore: new JsonDeadLetterStore<InputEventItem>(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Heartbeat",
                        "input-events-dead-letter.json")),
                    statusRegistry: sp.GetRequiredService<UploadStatusRegistry>(),
                    compatibilityStatus: sp.GetRequiredService<ClientCompatibilityStatus>());
            });

            // 自启动服务
            services.AddSingleton<IAutoStartService, RegistryAutoStartService>();

            // 托管后台服务。停止顺序为注册的逆序：AppMonitorService 必须最后注册、最先停止，
            // 使其终态快照先推入 hub，再由 UploadWorker.StopAsync 的最终 drain 带走（ADR-020）。
            // 此顺序由 AgentHostExtensionsTests 钉住。
            // 注意：IDisposable 的托管服务只通过 AddHostedService 注册一次。此前 AppMonitorService /
            // InputEventCollector 另有 AddSingleton 注册，容器把同一实例捕获进 disposables 两次，
            // host.Dispose() 双重 Dispose → 对已释放 CTS 调 Cancel 抛异常 → 退出流程中断、端口不释放。
            services.AddHostedService<InputEventCollector>();
            services.AddHostedService<AppMonitorService>();

            return services;
        }
    }
}
