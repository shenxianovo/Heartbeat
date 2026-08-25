using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Auth;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Heartbeat.Collection.Hub.Hosting;

/// <summary>
/// 可被桌面 Agent 或无头 host 复用的 hub 运行时组合入口。这里只注册进程内运行时状态；
/// HTTP transport、凭证、缓存路径与托管 worker 由各 composition root 提供。
/// </summary>
public static class HubServiceCollectionExtensions
{
    public static IServiceCollection AddHeartbeatHub(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ICollectorAppHintResolver, NullCollectorAppHintResolver>();
        services.TryAddSingleton<SegmentIngestService>();
        services.TryAddSingleton<ISegmentSink>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<IUploadSource<ActivitySegmentItem>>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<ICurrentActivitySink>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<ICollectionStatus>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<IHubRuntimeHooks, NullHubRuntimeHooks>();
        services.TryAddSingleton<IInputEventRecordingPolicy, EnabledInputEventRecordingPolicy>();
        services.TryAddSingleton<ClientCompatibilityStatus>();
        services.TryAddSingleton<IClientCompatibilityStatus>(sp =>
            sp.GetRequiredService<ClientCompatibilityStatus>());
        services.TryAddSingleton<UploadStatusRegistry>();
        services.TryAddSingleton<IUploadStatus>(sp => sp.GetRequiredService<UploadStatusRegistry>());
        services.TryAddSingleton<TokenManager>();
        services.TryAddSingleton<IAccessTokenProvider>(sp => sp.GetRequiredService<TokenManager>());
        services.AddHttpClient<AuthServiceClient>();
        services.AddTransient<BearerTokenHandler>();
        services.AddHttpClient<HeartbeatApiClient>().AddHttpMessageHandler<BearerTokenHandler>();
        services.TryAddSingleton<SegmentIngestRequestHandler>();
        services.TryAddSingleton<IExternalHostProtocolHttpHandler, NullExternalHostProtocolHttpHandler>();
        services.TryAddSingleton<DeclarationUplinkService>();
        services.AddHostedService<UploadWorker>();
        services.AddHostedService<StatusUploadWorker>();
        services.AddHostedService<SegmentIngestWorker>();
        return services;
    }

    public static IServiceCollection AddBrowserExternalHostBinding(
        this IServiceCollection services,
        BrowserExternalHostBindingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton(provider =>
        {
            var runtime = new BrowserCollectorRuntime(
                provider.GetRequiredService<CollectorRuntime>(),
                provider.GetRequiredService<ICollectorRegistry>(),
                provider.GetRequiredService<IDeviceIdentity>(),
                options);
            runtime.EnsureBundledPackageInstalled();
            return runtime;
        });
        services.AddSingleton<BrowserExternalHostProtocolHandler>();
        services.Replace(ServiceDescriptor.Singleton<IExternalHostProtocolHttpHandler>(provider =>
            provider.GetRequiredService<BrowserExternalHostProtocolHandler>()));
        services.AddHostedService<ExternalHostLeaseMonitor>();
        return services;
    }
}
