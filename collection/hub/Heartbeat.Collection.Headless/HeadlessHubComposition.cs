using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Headless;

public static class HeadlessHubComposition
{
    public static IServiceCollection AddHeartbeatHeadlessHub(
        this IServiceCollection services,
        HeadlessHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Directory.CreateDirectory(options.DataDirectory);

        services.AddHeartbeatHub();
        services.AddSingleton(options);
        services.AddSingleton<HeadlessHubConfiguration>();
        services.AddSingleton<IHubConfiguration>(provider => provider.GetRequiredService<HeadlessHubConfiguration>());
        services.AddSingleton<ICollectorRegistry>(provider => provider.GetRequiredService<HeadlessHubConfiguration>());
        services.AddSingleton<IDeviceIdentity>(provider => provider.GetRequiredService<HeadlessHubConfiguration>());
        services.AddSingleton<IUploadSource<InputEventItem>, EmptyInputEventSource>();
        services.AddSingleton<ICache<ActivitySegmentItem>>(_ => new JsonFileCache<ActivitySegmentItem>(
            Path.Combine(options.DataDirectory, "segments-cache.json"),
            20_000,
            HeartbeatCacheFormats.SegmentVersion2(),
            HeartbeatCacheFormats.SegmentMigrations()));
        services.AddSingleton<ICache<InputEventItem>>(_ => new JsonFileCache<InputEventItem>(
            Path.Combine(options.DataDirectory, "input-events-cache.json"),
            100_000,
            HeartbeatCacheFormats.InputEventVersion2(),
            HeartbeatCacheFormats.InputEventMigrations()));
        services.AddSingleton(provider => new UploadStream<ActivitySegmentItem>(
            "段",
            provider.GetRequiredService<IUploadSource<ActivitySegmentItem>>(),
            batch => provider.GetRequiredService<HeartbeatApiClient>()
                .UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
            provider.GetRequiredService<ICache<ActivitySegmentItem>>(),
            SnapshotCompaction.KeepLatest,
            new JsonDeadLetterStore<ActivitySegmentItem>(Path.Combine(options.DataDirectory, "segments-dead-letter.json")),
            provider.GetRequiredService<UploadStatusRegistry>(),
            provider.GetRequiredService<ClientCompatibilityStatus>()));
        services.AddSingleton(provider => new UploadStream<InputEventItem>(
            "输入事件",
            provider.GetRequiredService<IUploadSource<InputEventItem>>(),
            batch => provider.GetRequiredService<HeartbeatApiClient>()
                .UploadInputEventsAsync(new InputEventUploadRequest { Events = batch }),
            provider.GetRequiredService<ICache<InputEventItem>>(),
            deadLetterStore: new JsonDeadLetterStore<InputEventItem>(Path.Combine(options.DataDirectory, "input-events-dead-letter.json")),
            statusRegistry: provider.GetRequiredService<UploadStatusRegistry>(),
            compatibilityStatus: provider.GetRequiredService<ClientCompatibilityStatus>()));
        services.AddSingleton(provider => CollectorRuntime.Open(
            options.RuntimeStatePath,
            provider.GetRequiredService<ISegmentSink>()));

        services.AddHostedService<ManagedCollectorHostedService>();
        return services;
    }
}

public sealed class HeadlessHubConfiguration : IHubConfiguration, ICollectorRegistry, IDeviceIdentity
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CollectorRegistration> _collectors = new(StringComparer.Ordinal);

    public HeadlessHubConfiguration(HeadlessHubOptions options)
    {
        Current = new HubRuntimeSettings(options.ApiKey, TimeSpan.FromSeconds(options.UploadIntervalSeconds), 0);
        HardwareId = options.HubHardwareId;
        DeviceName = options.HubName;
    }

    public HubRuntimeSettings Current { get; }
    public event Action? Changed { add { } remove { } }
    public string HardwareId { get; }
    public string DeviceName { get; }
    public IReadOnlyDictionary<string, CollectorRegistration> Snapshot
    {
        get { lock (_gate) return new Dictionary<string, CollectorRegistration>(_collectors); }
    }

    public CollectorRegistration Touch(string source, int? flushPeriodMs = null)
    {
        lock (_gate)
        {
            if (!_collectors.TryGetValue(source, out var current))
                current = new CollectorRegistration(true, flushPeriodMs, null, null);
            else if (flushPeriodMs is not null)
                current = current with { FlushPeriodMs = flushPeriodMs };
            _collectors[source] = current;
            return current;
        }
    }

    public void Discover(IEnumerable<string> sources)
    {
        foreach (var source in sources)
            Touch(source);
    }

    public void StoreDeclaration(string source, string declarationJson, int version)
    {
        lock (_gate)
        {
            var current = Touch(source);
            _collectors[source] = current with
            {
                DeclarationJson = declarationJson,
                DeclarationVersion = version
            };
        }
    }
}

internal sealed class EmptyInputEventSource : IUploadSource<InputEventItem>
{
    public List<InputEventItem> Drain() => [];
    public void Reinject(List<InputEventItem> items) { }
}

public sealed class ManagedCollectorHostedService(
    HeadlessHubOptions options,
    CollectorRuntime runtime,
    IHostApplicationLifetime lifetime) : IHostedService
{
    private ManagedProcessCollectorActivation? _activation;

    public ManagedProcessCollectorActivation? Activation => _activation;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var package = LocalCollectorPackage.Load(options.PackageDirectory);
        var subject = new SubjectReference(options.SubjectId, SubjectKind.Account);
        var instances = runtime.FindInstances(package.Manifest.PackageId, subject);
        var instance = instances.Count switch
        {
            0 => runtime.CreateInstance(
                package,
                subject,
                new CollectorInstanceSpec(1, options.ConfigSchemaVersion, options.Config.Clone())),
            1 => instances[0],
            _ => throw new InvalidOperationException(
                "Headless Hub configuration resolves to more than one Collector Instance.")
        };
        _activation = await runtime.ActivateManagedProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ManagedProcessActivationOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
                DrainGracePeriod = TimeSpan.FromSeconds(options.DrainGraceSeconds)
            },
            cancellationToken);
        _ = StopHostOnUnexpectedExitAsync(_activation);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_activation is not null)
            await _activation.StopAsync(cancellationToken);
    }

    private async Task StopHostOnUnexpectedExitAsync(ManagedProcessCollectorActivation activation)
    {
        await activation.Completion;
        if (activation.RuntimeState.Phase == CollectorRuntimePhase.Failed)
            lifetime.StopApplication();
    }
}
