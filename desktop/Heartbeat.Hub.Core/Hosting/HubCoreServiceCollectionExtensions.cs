using Heartbeat.Hub.Core.Presence;
using Heartbeat.Hub.Core.Segments;
using Heartbeat.Hub.Core.Time;
using Heartbeat.Hub.Core.Upload;
using Heartbeat.Hub.Core.Auth;
using Heartbeat.Hub.Core.Collectors;
using Heartbeat.Hub.Core.Ingest;
using Heartbeat.Hub.Core.Http;
using Heartbeat.Hub.Core.Runtime;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Heartbeat.Hub.Core.Hosting;

/// <summary>
/// 可被桌面 Agent 或无头 host 复用的 hub 运行时组合入口。这里只注册进程内运行时状态；
/// HTTP transport、凭证、缓存路径与托管 worker 由各 composition root 提供。
/// </summary>
public static class HubCoreServiceCollectionExtensions
{
    public static IServiceCollection AddHeartbeatHubCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<SegmentIngestService>();
        services.TryAddSingleton<ISegmentSink>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<IUploadSource<ActivitySegmentItem>>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<ICurrentActivitySink>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<ICollectionStatus>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<IHubRuntimeHooks, NullHubRuntimeHooks>();
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
        services.TryAddSingleton<DeclarationUplinkService>();
        services.AddHostedService<UploadWorker>();
        services.AddHostedService<StatusUploadWorker>();
        services.AddHostedService<SegmentIngestWorker>();
        return services;
    }
}
