using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Ingest;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public enum SubjectKind
{
    Machine,
    Account,
    Person
}

public readonly record struct SubjectReference
{
    public SubjectReference(Guid subjectId, SubjectKind kind)
    {
        if (subjectId == Guid.Empty)
            throw new ArgumentException("SubjectId must not be empty.", nameof(subjectId));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        SubjectId = subjectId;
        Kind = kind;
    }

    public Guid SubjectId { get; }
    public SubjectKind Kind { get; }
}

public sealed record CollectorInstanceSpec(
    long SpecRevision,
    int ConfigSchemaVersion,
    JsonElement Config);

public sealed record CollectorInstance(
    Guid CollectorInstanceId,
    string PackageId,
    string PackageVersion,
    string PackageContentHash,
    SubjectReference Subject,
    CollectorInstanceSpec Spec);

public sealed class CollectorRuntimeOptions
{
    public Func<Guid> IdGenerator { get; init; } = Guid.CreateVersion7;
    public int MaxFactsPerBatch { get; init; } = 500;
    public int MaxBatchBytes { get; init; } = 1_048_576;
    public int MaxInFlightBatches { get; init; } = 2;
    public int MaxDurableFacts { get; init; } = 20_000;
    public int RetryAfterMilliseconds { get; init; } = 1_000;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(IdGenerator);
        if (MaxFactsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxFactsPerBatch));
        if (MaxBatchBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBatchBytes));
        if (MaxInFlightBatches <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxInFlightBatches));
        if (MaxDurableFacts <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxDurableFacts));
        if (RetryAfterMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(RetryAfterMilliseconds));
    }
}

/// <summary>
/// Collector Package/Instance/Activation runtime. Durable Instance and Fact Stream identity,
/// protocol convergence, and legacy Hub projection stay behind this module.
/// </summary>
public sealed partial class CollectorRuntime : IDisposable, IAsyncDisposable
{
    private const long MaxSafeJsonInteger = 9_007_199_254_740_991;
    private readonly object _gate = new();
    private readonly JsonCollectorRuntimeStore _store;
    private readonly ISegmentSink _segmentSink;
    private readonly CollectorRuntimeOptions _options;
    private readonly object _disposeGate = new();
    private CollectorRuntimeState _state;
    private Task? _disposeTask;
    private bool _disposing;
    private bool _disposed;

    private CollectorRuntime(
        JsonCollectorRuntimeStore store,
        ISegmentSink segmentSink,
        CollectorRuntimeOptions options,
        CollectorRuntimeState state,
        ICollectorAppHintResolver? appHintResolver)
    {
        _store = store;
        _segmentSink = segmentSink;
        _options = options;
        _state = state;
        _segmentProjectors = [new ActivitySegmentFactProjector(appHintResolver)];
    }

    public static CollectorRuntime Open(
        string stateFilePath,
        ISegmentSink segmentSink,
        CollectorRuntimeOptions? options = null,
        ICollectorAppHintResolver? appHintResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        ArgumentNullException.ThrowIfNull(segmentSink);
        options ??= new CollectorRuntimeOptions();
        options.Validate();

        var store = new JsonCollectorRuntimeStore(stateFilePath);
        try
        {
            var state = store.Load();
            var runtime = new CollectorRuntime(store, segmentSink, options, state, appHintResolver);
            runtime.RestorePersistedFactSchemas();
            runtime.ReplayCommittedSegments();
            return runtime;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public CollectorInstance CreateInstance(
        LocalCollectorPackage package,
        SubjectReference subject,
        CollectorInstanceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidateSpec(spec);

        lock (_gate)
        {
            ThrowIfDisposed();
            var knownFingerprint = KnownPackageFingerprint(
                package.Manifest.PackageId,
                package.Manifest.Version);
            if (knownFingerprint is not null && knownFingerprint != package.PackageContentHash)
                throw new InvalidOperationException(
                    "An immutable Collector Package version cannot resolve to a different content fingerprint.");
            var instanceId = _options.IdGenerator();
            if (!IsUuidV7(instanceId))
                throw new InvalidOperationException("Collector Runtime ID generator must return a UUIDv7.");
            if (_state.Instances.Any(instance => instance.CollectorInstanceId == instanceId))
                throw new InvalidOperationException($"Collector Instance '{instanceId}' already exists.");

            var instanceState = new CollectorInstanceState
            {
                CollectorInstanceId = instanceId,
                PackageId = package.Manifest.PackageId,
                PackageVersion = package.Manifest.Version,
                PackageContentHash = package.PackageContentHash,
                PackageFingerprints = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [package.Manifest.Version] = package.PackageContentHash
                },
                SubjectId = subject.SubjectId,
                SubjectKind = subject.Kind,
                SpecRevision = spec.SpecRevision,
                ConfigSchemaVersion = spec.ConfigSchemaVersion,
                Config = spec.Config.Clone()
            };
            var next = _state.WithInstance(instanceState);
            _store.Save(next);
            _state = next;
            return ToPublic(instanceState);
        }
    }

    public CollectorInstance GetInstance(Guid collectorInstanceId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var state = _state.Instances.SingleOrDefault(
                instance => instance.CollectorInstanceId == collectorInstanceId)
                ?? throw new KeyNotFoundException($"Collector Instance '{collectorInstanceId}' was not found.");
            return ToPublic(state);
        }
    }

    public IReadOnlyList<CollectorInstance> FindInstances(
        string packageId,
        SubjectReference subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _state.Instances
                .Where(instance =>
                    instance.PackageId == packageId &&
                    instance.SubjectId == subject.SubjectId &&
                    instance.SubjectKind == subject.Kind)
                .Select(ToPublic)
                .ToArray();
        }
    }

    public CollectorInstance UpdateInstanceSpec(
        Guid collectorInstanceId,
        int configSchemaVersion,
        JsonElement config)
    {
        if (configSchemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(configSchemaVersion));
        if (config.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Config must contain a JSON value.", nameof(config));

        lock (_gate)
        {
            ThrowIfDisposed();
            var current = GetInstanceStateLocked(collectorInstanceId);
            if (current.SpecRevision >= MaxSafeJsonInteger)
                throw new InvalidOperationException("Collector Instance SpecRevision cannot be incremented safely.");
            var updated = new CollectorInstanceState
            {
                CollectorInstanceId = current.CollectorInstanceId,
                PackageId = current.PackageId,
                PackageVersion = current.PackageVersion,
                PackageContentHash = current.PackageContentHash,
                PackageFingerprints = new Dictionary<string, string>(current.PackageFingerprints, StringComparer.Ordinal),
                SubjectId = current.SubjectId,
                SubjectKind = current.SubjectKind,
                SpecRevision = current.SpecRevision + 1,
                ConfigSchemaVersion = configSchemaVersion,
                Config = config.Clone()
            };
            var next = _state.WithInstanceAndStreams(updated, []);
            _store.Save(next);
            _state = next;
            return ToPublic(updated);
        }
    }

    private static void ValidateSpec(CollectorInstanceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.SpecRevision is <= 0 or > MaxSafeJsonInteger)
            throw new ArgumentOutOfRangeException(nameof(spec), "SpecRevision must be a positive JSON-safe integer.");
        if (spec.ConfigSchemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(spec), "ConfigSchemaVersion must be positive.");
        if (spec.Config.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Config must contain a JSON value.", nameof(spec));
    }

    private static CollectorInstance ToPublic(CollectorInstanceState state) => new(
        state.CollectorInstanceId,
        state.PackageId,
        state.PackageVersion,
        state.PackageContentHash,
        new SubjectReference(state.SubjectId, state.SubjectKind),
        new CollectorInstanceSpec(
            state.SpecRevision,
            state.ConfigSchemaVersion,
            state.Config.Clone()));

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null;
        Task disposeTask;
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = owner.Task;
            }
            disposeTask = _disposeTask;
        }
        if (owner is not null)
            _ = CompleteDisposeAsync(owner);
        return new ValueTask(disposeTask);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            lock (_disposeGate)
            {
                if (ReferenceEquals(_disposeTask, completion.Task))
                    _disposeTask = null;
            }
            completion.SetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        InProcessCollectorActivation[] activations;
        ExternalHostCollectorActivation[] externalHostActivations;
        StartingCollector[] startingCollectors;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposing = true;
            activations = _activations.Values
                .Where(activation => activation.State != CollectorActivationState.Stopped)
                .ToArray();
            externalHostActivations = _externalHostActivations.Values
                .Where(activation => activation.State != CollectorActivationState.Stopped)
                .ToArray();
            startingCollectors = _startingCollectors.Values.ToArray();
        }

        var stopFailures = new List<Exception>();
        foreach (var startingCollector in startingCollectors)
        {
            try
            {
                await startingCollector.StopAsync();
            }
            catch (Exception exception)
            {
                Serilog.Log.Warning(
                    exception,
                    "释放 Collector Runtime 时停止正在初始化的 Collector Instance {CollectorInstanceId} 失败",
                    startingCollector.CollectorInstanceId);
                stopFailures.Add(exception);
            }
        }
        if (stopFailures.Count != 0)
            throw new AggregateException("One or more Collectors did not stop; Runtime ownership is retained.", stopFailures);

        foreach (var startingCollector in startingCollectors)
            await startingCollector.ActivationCompleted;

        foreach (var activation in activations)
        {
            try
            {
                await activation.StopAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Serilog.Log.Warning(
                    exception,
                    "释放 Collector Runtime 时停止 Activation {ActivationId} 失败",
                    activation.ActivationId);
                stopFailures.Add(exception);
            }
        }

        foreach (var activation in externalHostActivations)
            StopExternalHostActivation(activation, ExternalHostActivationStopReason.RuntimeStopping);

        if (stopFailures.Count != 0)
            throw new AggregateException("One or more Collectors did not stop; Runtime ownership is retained.", stopFailures);

        lock (_gate)
        {
            foreach (var startingCollector in startingCollectors)
            {
                if (_startingCollectors.TryGetValue(startingCollector.CollectorInstanceId, out var registered) &&
                    ReferenceEquals(registered, startingCollector))
                    _startingCollectors.Remove(startingCollector.CollectorInstanceId);
                _startingInstances.Remove(startingCollector.CollectorInstanceId);
            }
            _streamWriters.Clear();
            _store.Dispose();
            _disposed = true;
            _disposing = false;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed || _disposing, this);

    private void ThrowIfDeliveryUnavailable(Guid activationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_disposing &&
            (!_activations.TryGetValue(activationId, out var activation) ||
             activation.State != CollectorActivationState.Draining))
            throw new ObjectDisposedException(nameof(CollectorRuntime));
    }

    private static bool IsUuidV7(Guid id)
    {
        var text = id.ToString("D");
        return id != Guid.Empty && text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }
}
