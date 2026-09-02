using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Auth;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Http;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Headless;

public sealed record HeadlessSubjectStatusResponse(
    Guid SubjectId,
    string SubjectName,
    string SubjectKind,
    Guid? CollectorInstanceId,
    string PackageVersion,
    string PackageContentHash,
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
    private readonly CancellationTokenSource _activationCancellation = new();
    private readonly List<IDisposable> _ownedDisposables = [];
    private CollectorRuntime? _runtime;
    private HeadlessInstancePipelines? _pipelines;

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
                var instance = RuntimeInstance(entry.CollectorInstanceId);
                return new HeadlessSubjectStatusResponse(
                    entry.Options.SubjectId,
                    entry.Options.SubjectName,
                    entry.Options.SubjectKind.ToString(),
                    entry.CollectorInstanceId,
                    instance?.PackageVersion ?? entry.Package.Manifest.Version,
                    instance?.PackageContentHash ?? entry.Package.PackageContentHash,
                    runtime?.Phase.ToString() ?? "Starting",
                    runtime?.AuthorizationChallenge,
                    _pipelines?.CurrentActivity(entry.CollectorInstanceId));
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
        _pipelines?.Dispose();
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
        var pipelines = new HeadlessInstancePipelines(
            options.DataDirectory,
            new HeadlessAnalyticsSegmentUploadAdapter(tokens));
        _pipelines = pipelines;
        var registeredPipelineIds = new HashSet<Guid>();

        foreach (var configured in options.Instances)
        {
            if (!mappings.TryGetValue(configured.InstanceKey, out var instanceId))
                continue;
            pipelines.Add(instanceId, configured);
            registeredPipelineIds.Add(instanceId);
        }

        _runtime = CollectorRuntime.Open(
            Path.Combine(options.DataDirectory, "collector-runtime.json"),
            pipelines,
            secretStore: new EncryptedFileCollectorSecretStore(
                Path.Combine(options.DataDirectory, "collector-secrets")));

        var claimedInstanceIds = mappings.Values.ToHashSet();
        // 配置里的 packageDirectory 是宿主挂载的 Package 来源，只读。运行永远发生在 Installation 上，
        // 所以先安装再打开，来源目录不充当运行时可变目录。
        var installations = new CollectorPackageInstallations(
            Path.Combine(options.DataDirectory, "collector-packages"));
        foreach (var configured in options.Instances)
        {
            var package = installations.Install(configured.PackageDirectory).Package;
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

            if (registeredPipelineIds.Add(instance.CollectorInstanceId))
                pipelines.Add(instance.CollectorInstanceId, configured);
            _entries.Add(new Entry(configured, package, instance.CollectorInstanceId));
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

    private CollectorInstance? RuntimeInstance(Guid collectorInstanceId)
    {
        if (_runtime is null) return null;
        try { return _runtime.GetInstance(collectorInstanceId); }
        catch (KeyNotFoundException) { return null; }
    }

    private async Task DrainPipelinesAsync()
    {
        if (_pipelines is not null)
            await _pipelines.DrainAllAsync();
    }

    private Dictionary<string, Guid> LoadInstanceMappings()
    {
        var path = InstanceMapPath;
        if (!File.Exists(path)) return new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var mappings = DeserializeInstanceMappings(File.ReadAllText(path), out var legacy);
        if (legacy)
            SaveInstanceMappings(mappings);
        return mappings;
    }

    internal static Dictionary<string, Guid> DeserializeInstanceMappings(string json, out bool legacy)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Headless Instance mapping must be a JSON object.");
        if (!document.RootElement.TryGetProperty("schemaVersion", out _))
        {
            var legacyMappings = document.RootElement.Deserialize<Dictionary<string, Guid>>(StateJsonOptions)
                                 ?? throw new JsonException("Headless Instance mapping is empty.");
            legacy = true;
            return new Dictionary<string, Guid>(legacyMappings, StringComparer.OrdinalIgnoreCase);
        }
        var state = document.RootElement.Deserialize<InstanceMapState>(StateJsonOptions)
                    ?? throw new JsonException("Headless Instance mapping is empty.");
        if (state.SchemaVersion != 1 || state.Mappings is null)
            throw new JsonException($"Unsupported Headless Instance mapping schemaVersion {state.SchemaVersion}.");
        legacy = false;
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
        Guid collectorInstanceId)
    {
        public HeadlessManagedInstanceOptions Options { get; } = options;
        public LocalCollectorPackage Package { get; } = package;
        public Guid CollectorInstanceId { get; } = collectorInstanceId;
        public Task? ActivationTask { get; set; }
        public ManagedProcessCollectorActivation? Activation { get; set; }
    }

    private sealed class FleetHubConfiguration(HeadlessFleetOptions options) : IHubConfiguration
    {
        public HubRuntimeSettings Current { get; } = new(
            options.ApiKey,
            TimeSpan.FromSeconds(options.UploadIntervalSeconds),
            0);
        public event Action? Changed { add { } remove { } }
    }
}
