using Heartbeat.Desktop.Mac.Collectors;
using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Identity;
using Heartbeat.Desktop.Mac.Icons;
using Heartbeat.Desktop.Mac.Native;
using Heartbeat.Desktop.Mac.Observations;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Configuration;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Heartbeat.Desktop.Mac.Hosting;

public static class MacAgentHostExtensions
{
    public static IServiceCollection AddHeartbeatMacAgent(
        this IServiceCollection services,
        MacAgentPaths? paths = null)
    {
        paths ??= MacAgentPaths.Default;
        services.AddSingleton(paths);
        services.AddHeartbeatHub();

        services.TryAddSingleton<MacConfigManager>();
        services.TryAddSingleton<IMacCommandRunner, MacCommandRunner>();
        services.TryAddSingleton<IMacPlatformUuid, IoregPlatformUuid>();
        services.TryAddSingleton(sp => new MacMachineIdentity(sp.GetRequiredService<IMacPlatformUuid>()));
        services.TryAddSingleton<IMacWorkspaceNative, CocoaWorkspaceNative>();
        services.TryAddSingleton<MacApplicationCatalog>();
        services.TryAddSingleton<MacWorkspaceEvents>();
        services.TryAddSingleton<IMacDesktopEvents>(sp => sp.GetRequiredService<MacWorkspaceEvents>());
        services.TryAddSingleton<MacDesktopObservationSource>();
        services.TryAddSingleton<IDesktopObservationSource>(sp =>
            sp.GetRequiredService<MacDesktopObservationSource>());

        services.TryAddSingleton<MacHubConfiguration>(sp =>
            new MacHubConfiguration(sp.GetRequiredService<MacConfigManager>()).Initialize());
        services.TryAddSingleton<IHubConfiguration>(sp => sp.GetRequiredService<MacHubConfiguration>());
        services.TryAddSingleton<ICollectorRegistry>(sp => sp.GetRequiredService<MacHubConfiguration>());
        services.Replace(ServiceDescriptor.Singleton<ICollectorAppHintResolver, MacCollectorAppHintResolver>());

        services.TryAddSingleton<MacDesktopSettings>(sp =>
            new MacDesktopSettings(sp.GetRequiredService<MacConfigManager>()).Initialize());
        services.TryAddSingleton<IDesktopSettings>(sp => sp.GetRequiredService<MacDesktopSettings>());
        services.Replace(ServiceDescriptor.Singleton<IInputEventRecordingPolicy, MacInputEventRecordingPolicy>());
        services.TryAddSingleton<IInputActivitySignal, InputActivitySignal>();
        services.TryAddSingleton<IDeviceIdentity, MacDeviceIdentity>();
        services.TryAddSingleton<IMacIconTools, MacIconTools>();
        services.TryAddSingleton<MacBundleIconExtractor>();
        services.TryAddSingleton<MacIconUploadService>();
        services.Replace(ServiceDescriptor.Singleton<IHubRuntimeHooks, MacHubRuntimeHooks>());

        services.TryAddSingleton<ICache<ActivitySegmentItem>>(sp =>
            new JsonFileCache<ActivitySegmentItem>(
                Path.Combine(sp.GetRequiredService<MacAgentPaths>().DataDirectory, "segments-cache.json"),
                20_000,
                HeartbeatCacheFormats.SegmentVersion2(),
                HeartbeatCacheFormats.SegmentMigrations()));
        services.TryAddSingleton<ICache<InputEventItem>>(sp =>
            new JsonFileCache<InputEventItem>(
                Path.Combine(sp.GetRequiredService<MacAgentPaths>().DataDirectory, "input-events-cache.json"),
                100_000,
                HeartbeatCacheFormats.InputEventVersion2(),
                HeartbeatCacheFormats.InputEventMigrations()));

        services.TryAddSingleton(sp => new InputEventBuffer(sp.GetRequiredService<IClock>()));
        services.TryAddSingleton<IUploadSource<InputEventItem>>(sp => sp.GetRequiredService<InputEventBuffer>());
        services.TryAddSingleton(sp =>
        {
            var root = sp.GetRequiredService<MacAgentPaths>().DataDirectory;
            return new UploadStream<ActivitySegmentItem>(
                "段",
                sp.GetRequiredService<IUploadSource<ActivitySegmentItem>>(),
                batch => sp.GetRequiredService<HeartbeatApiClient>()
                    .UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
                sp.GetRequiredService<ICache<ActivitySegmentItem>>(),
                Heartbeat.Core.SnapshotCompaction.KeepLatest,
                new JsonDeadLetterStore<ActivitySegmentItem>(Path.Combine(root, "segments-dead-letter.json")),
                sp.GetRequiredService<UploadStatusRegistry>(),
                sp.GetRequiredService<ClientCompatibilityStatus>());
        });
        services.TryAddSingleton(sp =>
        {
            var root = sp.GetRequiredService<MacAgentPaths>().DataDirectory;
            return new UploadStream<InputEventItem>(
                "输入事件",
                sp.GetRequiredService<IUploadSource<InputEventItem>>(),
                batch => sp.GetRequiredService<HeartbeatApiClient>()
                    .UploadInputEventsAsync(new InputEventUploadRequest { Events = batch }),
                sp.GetRequiredService<ICache<InputEventItem>>(),
                deadLetterStore: new JsonDeadLetterStore<InputEventItem>(Path.Combine(root, "input-events-dead-letter.json")),
                statusRegistry: sp.GetRequiredService<UploadStatusRegistry>(),
                compatibilityStatus: sp.GetRequiredService<ClientCompatibilityStatus>());
        });

        // AddHeartbeatHub registers workers first. Monitor stays last so its terminal
        // snapshot is available before UploadWorker performs the final drain.
        services.AddHostedService<AppMonitorService>();
        return services;
    }
}
