using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Auth;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Headless;

public sealed record HeadlessSubjectStatusResponse(
    Guid SubjectId,
    string SubjectName,
    string SubjectKind,
    Guid? CollectorInstanceId,
    string Phase,
    CollectorAuthorizationChallenge? Authorization,
    HeadlessCurrentSubjectActivity? CurrentActivity);

/// <summary>
/// One Hub Runtime hosting every configured Collector Instance. Subject-aware projection routes
/// legacy Analytics uploads into per-Instance identity pipelines without splitting the Hub itself.
/// </summary>
public sealed class HeadlessFleetManager(
    HeadlessFleetOptions options) : BackgroundService
{
    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly HeadlessSubjectRouter _router = new();
    private readonly CancellationTokenSource _activationCancellation = new();
    private readonly List<IDisposable> _ownedDisposables = [];
    private CollectorRuntime? _runtime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Initialize();
        foreach (var entry in _entries)
            entry.ActivationTask = ActivateAsync(entry, _activationCancellation.Token);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            _activationCancellation.Token);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.UploadIntervalSeconds), linked.Token);
                await DrainPipelinesAsync();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public IReadOnlyList<HeadlessSubjectStatusResponse> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Select(entry =>
            {
                var runtime = RuntimeState(entry.CollectorInstanceId);
                return new HeadlessSubjectStatusResponse(
                    entry.Options.SubjectId,
                    entry.Options.SubjectName,
                    entry.Options.SubjectKind.ToString(),
                    entry.CollectorInstanceId,
                    runtime?.Phase.ToString() ?? "Starting",
                    runtime?.AuthorizationChallenge,
                    entry.Pipeline.Status.Current);
            }).ToArray();
        }
    }

    public async ValueTask SubmitAuthorizationAsync(
        Guid collectorInstanceId,
        Guid interactionId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var runtime = _runtime ?? throw new InvalidOperationException("Hub Runtime is not initialized.");
        lock (_gate)
        {
            if (_entries.All(entry => entry.CollectorInstanceId != collectorInstanceId))
                throw new KeyNotFoundException(
                    $"Collector Instance '{collectorInstanceId:D}' is not managed by this Hub.");
        }
        var state = runtime.GetManagedProcessRuntimeState(collectorInstanceId);
        if (state.AuthorizationChallenge?.InteractionId != interactionId)
            throw new InvalidOperationException("Authorization interaction is no longer current.");
        await runtime.SubmitManagedProcessAuthorizationAsync(
            collectorInstanceId,
            interactionId,
            values,
            cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _activationCancellation.Cancel();
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        foreach (var entry in entries)
        {
            if (entry.Activation is not null)
                await entry.Activation.StopAsync(cancellationToken);
            else if (entry.ActivationTask is not null)
            {
                try { await entry.ActivationTask.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { }
                catch { }
            }
        }
        await DrainPipelinesAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _activationCancellation.Dispose();
        _runtime?.Dispose();
        foreach (var disposable in _ownedDisposables) disposable.Dispose();
        base.Dispose();
    }

    private void Initialize()
    {
        options.Validate();
        Directory.CreateDirectory(options.DataDirectory);
        var configuration = new FleetHubConfiguration(options);
        var authHttp = new HttpClient();
        _ownedDisposables.Add(authHttp);
        var tokens = new TokenManager(configuration, new AuthServiceClient(authHttp));
        var mappings = LoadInstanceMappings();

        foreach (var configured in options.Instances)
        {
            if (!mappings.TryGetValue(configured.InstanceKey, out var instanceId))
                continue;
            var pipeline = CreatePipeline(configured, instanceId, tokens);
            _router.Add(instanceId, pipeline);
        }

        _runtime = CollectorRuntime.Open(
            Path.Combine(options.DataDirectory, "collector-runtime.json"),
            _router,
            secretStore: new EncryptedFileCollectorSecretStore(
                Path.Combine(options.DataDirectory, "collector-secrets")));

        var claimedInstanceIds = mappings.Values.ToHashSet();
        foreach (var configured in options.Instances)
        {
            var package = LocalCollectorPackage.Load(configured.PackageDirectory);
            CollectorInstance instance;
            if (mappings.TryGetValue(configured.InstanceKey, out var mappedId))
            {
                instance = _runtime.GetInstance(mappedId);
                if (instance.PackageId != package.Manifest.PackageId ||
                    instance.Subject != new SubjectReference(configured.SubjectId, configured.SubjectKind))
                    throw new InvalidOperationException(
                        $"Configured Instance key '{configured.InstanceKey}' changed its Package or Subject identity.");
                if (instance.Spec.ConfigVersion != configured.ConfigVersion ||
                    !JsonElement.DeepEquals(instance.Spec.Config, configured.Config))
                    instance = _runtime.UpdateInstanceSpec(
                        instance.CollectorInstanceId,
                        configured.ConfigVersion,
                        configured.Config);
            }
            else
            {
                var subject = new SubjectReference(configured.SubjectId, configured.SubjectKind);
                var recoverable = _runtime.FindInstances(package.Manifest.PackageId, subject)
                    .Where(candidate => !claimedInstanceIds.Contains(candidate.CollectorInstanceId))
                    .Where(candidate =>
                        candidate.Spec.ConfigVersion == configured.ConfigVersion &&
                        JsonElement.DeepEquals(candidate.Spec.Config, configured.Config))
                    .ToArray();
                instance = recoverable.Length switch
                {
                    0 => _runtime.CreateInstance(
                        package,
                        subject,
                        new CollectorInstanceSpec(1, configured.ConfigVersion, configured.Config.Clone())),
                    1 => recoverable[0],
                    _ => throw new InvalidOperationException(
                        $"Instance key '{configured.InstanceKey}' has multiple unmapped Runtime candidates.")
                };
                mappings[configured.InstanceKey] = instance.CollectorInstanceId;
                claimedInstanceIds.Add(instance.CollectorInstanceId);
                SaveInstanceMappings(mappings);
            }

            var pipeline = _router.Find(instance.CollectorInstanceId)
                ?? CreateAndAddPipeline(configured, instance.CollectorInstanceId, tokens);
            _entries.Add(new Entry(configured, package, instance.CollectorInstanceId, pipeline));
        }
    }

    private async Task ActivateAsync(Entry entry, CancellationToken cancellationToken)
    {
        try
        {
            var activation = await _runtime!.ActivateManagedProcessAsync(
                entry.CollectorInstanceId,
                entry.Package,
                new ManagedProcessActivationOptions
                {
                    StartupTimeout = TimeSpan.FromSeconds(entry.Options.StartupTimeoutSeconds),
                    DrainGracePeriod = TimeSpan.FromSeconds(entry.Options.DrainGraceSeconds)
                },
                cancellationToken);
            lock (_gate) entry.Activation = activation;
            await activation.Completion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            // CollectorRuntime owns the structured failure state exposed by Snapshot().
        }
    }

    private CollectorRuntimeSnapshot? RuntimeState(Guid collectorInstanceId)
    {
        if (_runtime is null) return null;
        try { return _runtime.GetManagedProcessRuntimeState(collectorInstanceId); }
        catch (KeyNotFoundException) { return null; }
    }

    private async Task DrainPipelinesAsync()
    {
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        foreach (var entry in entries)
            await entry.Pipeline.Upload.DrainAsync();
    }

    private InstancePipeline CreateAndAddPipeline(
        HeadlessManagedInstanceOptions configured,
        Guid instanceId,
        TokenManager tokens)
    {
        var pipeline = CreatePipeline(configured, instanceId, tokens);
        _router.Add(instanceId, pipeline);
        return pipeline;
    }

    private InstancePipeline CreatePipeline(
        HeadlessManagedInstanceOptions configured,
        Guid instanceId,
        TokenManager tokens)
    {
        var directory = Path.Combine(
            options.DataDirectory,
            "instances",
            instanceId.ToString("D"));
        Directory.CreateDirectory(directory);
        var ingest = new SegmentIngestService(new SystemClock());
        var identity = new FixedDeviceIdentity(
            $"subject:{configured.SubjectKind.ToString().ToLowerInvariant()}:{configured.SubjectId:D}",
            configured.SubjectName);
        var handler = new BearerTokenHandler(tokens, identity) { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler, disposeHandler: true);
        _ownedDisposables.Add(http);
        var api = new HeartbeatApiClient(http);
        var statusRegistry = new UploadStatusRegistry();
        var compatibility = new ClientCompatibilityStatus();
        var upload = new UploadStream<ActivitySegmentItem>(
            $"段/{configured.InstanceKey}",
            (IUploadSource<ActivitySegmentItem>)ingest,
            batch => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
            new JsonFileCache<ActivitySegmentItem>(
                Path.Combine(directory, "segments-cache.json"),
                20_000,
                HeartbeatCacheFormats.SegmentVersion2(),
                HeartbeatCacheFormats.SegmentMigrations()),
            SnapshotCompaction.KeepLatest,
            new JsonDeadLetterStore<ActivitySegmentItem>(
                Path.Combine(directory, "segments-dead-letter.json")),
            statusRegistry,
            compatibility);
        return new InstancePipeline(ingest, new HeadlessSubjectStatus(), upload);
    }

    private Dictionary<string, Guid> LoadInstanceMappings()
    {
        var path = InstanceMapPath;
        if (!File.Exists(path)) return new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var state = JsonSerializer.Deserialize<InstanceMapState>(File.ReadAllText(path), StateJsonOptions)
                    ?? throw new JsonException("Headless Instance mapping is empty.");
        if (state.SchemaVersion != 1 || state.Mappings is null)
            throw new JsonException($"Unsupported Headless Instance mapping schemaVersion {state.SchemaVersion}.");
        return new Dictionary<string, Guid>(state.Mappings, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveInstanceMappings(IReadOnlyDictionary<string, Guid> mappings)
    {
        var temporary = InstanceMapPath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                new InstanceMapState(1, new Dictionary<string, Guid>(mappings)),
                StateJsonOptions),
            new UTF8Encoding(false));
        File.Move(temporary, InstanceMapPath, overwrite: true);
    }

    private string InstanceMapPath => Path.Combine(options.DataDirectory, "headless-instance-map.json");

    private sealed record InstanceMapState(int SchemaVersion, Dictionary<string, Guid> Mappings);

    private sealed class Entry(
        HeadlessManagedInstanceOptions options,
        LocalCollectorPackage package,
        Guid collectorInstanceId,
        InstancePipeline pipeline)
    {
        public HeadlessManagedInstanceOptions Options { get; } = options;
        public LocalCollectorPackage Package { get; } = package;
        public Guid CollectorInstanceId { get; } = collectorInstanceId;
        public InstancePipeline Pipeline { get; } = pipeline;
        public Task? ActivationTask { get; set; }
        public ManagedProcessCollectorActivation? Activation { get; set; }
    }

    private sealed record FixedDeviceIdentity(string HardwareId, string DeviceName) : IDeviceIdentity;

    private sealed class FleetHubConfiguration(HeadlessFleetOptions options) : IHubConfiguration
    {
        public HubRuntimeSettings Current { get; } = new(
            options.ApiKey,
            TimeSpan.FromSeconds(options.UploadIntervalSeconds),
            0);
        public event Action? Changed { add { } remove { } }
    }
}

internal sealed record InstancePipeline(
    SegmentIngestService Ingest,
    HeadlessSubjectStatus Status,
    UploadStream<ActivitySegmentItem> Upload);

internal sealed class HeadlessSubjectRouter : ISegmentSink, ISubjectSegmentProjectionSink
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, InstancePipeline> _pipelines = [];

    public void Add(Guid collectorInstanceId, InstancePipeline pipeline)
    {
        lock (_gate)
        {
            if (!_pipelines.TryAdd(collectorInstanceId, pipeline))
                throw new InvalidOperationException(
                    $"Collector Instance '{collectorInstanceId:D}' already has a projection pipeline.");
        }
    }

    public InstancePipeline? Find(Guid collectorInstanceId)
    {
        lock (_gate)
            return _pipelines.GetValueOrDefault(collectorInstanceId);
    }

    public void Push(List<ActivitySegmentItem> snapshots) =>
        throw new NotSupportedException("Multi-Subject projection requires Collector Instance context.");

    public void UpsertDurable(
        CollectorProjectionContext context,
        ActivitySegmentItem snapshot,
        long revision,
        bool isFinal)
    {
        var pipeline = Required(context.CollectorInstanceId);
        pipeline.Ingest.UpsertDurable(snapshot, revision);
        pipeline.Status.Observe(snapshot, isFinal);
    }

    public void ReplayDurable(
        CollectorProjectionContext context,
        ActivitySegmentItem snapshot,
        long revision,
        bool isFinal)
    {
        var pipeline = Required(context.CollectorInstanceId);
        pipeline.Ingest.ReplayDurable(snapshot, revision);
        pipeline.Status.Observe(snapshot, isFinal);
    }

    public void RetractDurable(CollectorProjectionContext context, Guid segmentId, long revision)
    {
        var pipeline = Required(context.CollectorInstanceId);
        pipeline.Ingest.RetractDurable(segmentId, revision);
        pipeline.Status.Retract(segmentId);
    }

    private InstancePipeline Required(Guid collectorInstanceId)
    {
        lock (_gate)
            return _pipelines.TryGetValue(collectorInstanceId, out var pipeline)
                ? pipeline
                : throw new KeyNotFoundException(
                    $"Collector Instance '{collectorInstanceId:D}' has no projection pipeline.");
    }
}
