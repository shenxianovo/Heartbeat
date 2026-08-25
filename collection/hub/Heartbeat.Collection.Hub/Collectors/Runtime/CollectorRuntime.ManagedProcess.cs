using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Serilog;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public enum CollectorRuntimePhase
{
    Starting,
    Negotiating,
    OpeningStreams,
    Ready,
    Draining,
    Stopped,
    Failed
}

public sealed record CollectorRuntimeFailure(string Code, string Message, int? ProcessExitCode = null);

public sealed record CollectorRuntimeSnapshot(
    Guid CollectorInstanceId,
    Guid? ActivationId,
    CollectorRuntimePhase Phase,
    CollectorRuntimeFailure? Failure = null,
    int? PendingFacts = null,
    int? PendingGaps = null,
    bool ProcessTerminated = false);

public sealed class ManagedProcessActivationOptions
{
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DrainGracePeriod { get; init; } = TimeSpan.FromSeconds(10);
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    internal void Validate()
    {
        if (StartupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout));
        if (DrainGracePeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(DrainGracePeriod));
        ArgumentNullException.ThrowIfNull(EnvironmentVariables);
        if (EnvironmentVariables.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("ManagedProcess environment variables must have non-empty names and values.");
    }
}

public sealed partial class CollectorRuntime
{
    private readonly Dictionary<Guid, ManagedProcessCollectorActivation> _managedProcessActivations = [];
    private readonly Dictionary<Guid, CollectorRuntimeSnapshot> _managedProcessStates = [];

    public CollectorRuntimeSnapshot GetManagedProcessRuntimeState(Guid collectorInstanceId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _managedProcessStates.TryGetValue(collectorInstanceId, out var state)
                ? state
                : throw new KeyNotFoundException(
                    $"ManagedProcess Runtime State for Collector Instance '{collectorInstanceId}' was not found.");
        }
    }

    public ValueTask<ManagedProcessCollectorActivation> ActivateManagedProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        CancellationToken cancellationToken = default) =>
        ActivateManagedProcessAsync(collectorInstanceId, package, new ManagedProcessActivationOptions(), cancellationToken);

    public async ValueTask<ManagedProcessCollectorActivation> ActivateManagedProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        ManagedProcessActivationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        VerifiedCollectorArtifact artifact;
        lock (_gate)
        {
            ThrowIfDisposed();
            var instance = GetInstanceStateLocked(collectorInstanceId);
            ValidatePackageCandidate(instance, package);
            artifact = ResolveManagedProcessArtifact(package);
            _managedProcessStates[collectorInstanceId] = new CollectorRuntimeSnapshot(
                collectorInstanceId, null, CollectorRuntimePhase.Starting);
        }

        ManagedProcessProtocolClient? client = null;
        using var startupTimeout = new CancellationTokenSource(options.StartupTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, startupTimeout.Token);
        try
        {
            client = await ManagedProcessProtocolClient.StartAsync(
                package, artifact, collectorInstanceId, options, linkedCancellation.Token);
            client.SetSelectedCapabilities(SelectedCapabilities(package, client.ProtocolSupport));
            SetManagedProcessState(collectorInstanceId, new CollectorRuntimeSnapshot(
                collectorInstanceId, null, CollectorRuntimePhase.Negotiating));

            var protocolActivation = await ActivateInProcessAsync(
                collectorInstanceId, package, client, client.HelloMessageId, linkedCancellation.Token);
            var activation = new ManagedProcessCollectorActivation(
                this, collectorInstanceId, client, protocolActivation);
            lock (_gate)
            {
                _managedProcessActivations[protocolActivation.ActivationId] = activation;
                _managedProcessStates[collectorInstanceId] = new CollectorRuntimeSnapshot(
                    collectorInstanceId, protocolActivation.ActivationId, CollectorRuntimePhase.Ready);
            }
            activation.StartSupervision();
            return activation;
        }
        catch (OperationCanceledException exception) when (
            startupTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (client is not null)
                await client.AbortAsync();
            var failure = new CollectorRuntimeFailure(
                "activation_start_timeout",
                $"ManagedProcess Collector did not reach Ready within {options.StartupTimeout}.");
            SetManagedProcessState(collectorInstanceId, new CollectorRuntimeSnapshot(
                collectorInstanceId, client?.ActivationId, CollectorRuntimePhase.Failed, failure,
                ProcessTerminated: client?.WasTerminated == true));
            throw ActivationError(failure.Code, failure.Message, exception, retryable: true);
        }
        catch (Exception exception)
        {
            if (client is not null)
                await client.AbortAsync();
            var failure = exception switch
            {
                CollectorActivationException activationException => new CollectorRuntimeFailure(
                    ContainsProcessExit(activationException)
                        ? "process_exited"
                        : activationException.Error.Code,
                    ContainsProcessExit(activationException)
                        ? "ManagedProcess Collector exited before reaching Ready."
                        : activationException.Error.Message,
                    client?.ExitCode ?? FindProcessExit(activationException)?.ExitCode),
                ManagedProcessExitedException exitedException => new CollectorRuntimeFailure(
                    "process_exited", exitedException.Message, exitedException.ExitCode),
                ManagedProcessProtocolException protocolException => new CollectorRuntimeFailure(
                    "protocol_invalid_message", protocolException.Message, client?.ExitCode),
                _ => new CollectorRuntimeFailure("process_start_failed", exception.Message, client?.ExitCode)
            };
            SetManagedProcessState(collectorInstanceId, new CollectorRuntimeSnapshot(
                collectorInstanceId, client?.ActivationId, CollectorRuntimePhase.Failed, failure,
                ProcessTerminated: client?.WasTerminated == true));
            if (exception is CollectorActivationException originalActivationException &&
                failure.Code == originalActivationException.Error.Code)
                throw;
            throw ActivationError(failure.Code, failure.Message, exception, retryable: true);
        }
    }

    internal void ManagedProcessDraining(ManagedProcessCollectorActivation activation)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _managedProcessStates[activation.CollectorInstanceId] = activation.RuntimeState with
            {
                Phase = CollectorRuntimePhase.Draining
            };
        }
    }

    internal void ManagedProcessStopped(ManagedProcessCollectorActivation activation, ManagedProcessDrainResult result)
    {
        lock (_gate)
        {
            _managedProcessActivations.Remove(activation.ActivationId);
            _managedProcessStates[activation.CollectorInstanceId] = new CollectorRuntimeSnapshot(
                activation.CollectorInstanceId, activation.ActivationId, CollectorRuntimePhase.Stopped,
                PendingFacts: result.PendingFacts, PendingGaps: result.PendingGaps,
                ProcessTerminated: result.ProcessTerminated);
        }
    }

    internal void ManagedProcessFailed(ManagedProcessCollectorActivation activation, ManagedProcessExit result)
    {
        var failure = new CollectorRuntimeFailure(
            result.ProtocolError is null ? "process_exited" : "protocol_invalid_message",
            result.ProtocolError?.Message ??
            $"ManagedProcess Collector exited before drain completed (exit code {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}).",
            result.ExitCode);
        lock (_gate)
        {
            _managedProcessActivations.Remove(activation.ActivationId);
            _managedProcessStates[activation.CollectorInstanceId] = new CollectorRuntimeSnapshot(
                activation.CollectorInstanceId, activation.ActivationId, CollectorRuntimePhase.Failed,
                failure, activation.Client.PendingFacts, activation.Client.PendingGaps,
                activation.Client.WasTerminated);
        }
    }

    private void SetManagedProcessState(Guid collectorInstanceId, CollectorRuntimeSnapshot state)
    {
        lock (_gate)
            _managedProcessStates[collectorInstanceId] = state;
    }

    private static bool ContainsProcessExit(Exception exception) =>
        FindProcessExit(exception) is not null;

    private static ManagedProcessExitedException? FindProcessExit(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ManagedProcessExitedException exited)
                return exited;
        }
        return null;
    }

    private static VerifiedCollectorArtifact ResolveManagedProcessArtifact(LocalCollectorPackage package) =>
        ResolveProtocolArtifact(package, "managedProcess");
}

public sealed class ManagedProcessCollectorActivation : IAsyncDisposable
{
    private readonly CollectorRuntime _runtime;
    private readonly InProcessCollectorActivation _protocolActivation;
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private int _stopRequested;

    internal ManagedProcessCollectorActivation(
        CollectorRuntime runtime,
        Guid collectorInstanceId,
        ManagedProcessProtocolClient client,
        InProcessCollectorActivation protocolActivation)
    {
        _runtime = runtime;
        CollectorInstanceId = collectorInstanceId;
        Client = client;
        _protocolActivation = protocolActivation;
    }

    public Guid CollectorInstanceId { get; }
    public Guid ActivationId => _protocolActivation.ActivationId;
    public int ProcessId => Client.ProcessId;
    public CollectorActivationState State => _protocolActivation.State;
    public ActivationDeliveryCapability DeliveryCapability => _protocolActivation.DeliveryCapability;
    public IReadOnlyList<CollectorHandshakeStep> HandshakeTranscript => _protocolActivation.HandshakeTranscript;
    public IReadOnlyDictionary<string, FactStreamDescriptor> Streams => _protocolActivation.Streams
        .ToDictionary(pair => pair.Key, pair => pair.Value.Descriptor, StringComparer.Ordinal);
    public CollectorRuntimeSnapshot RuntimeState => _runtime.GetManagedProcessRuntimeState(CollectorInstanceId);
    public Task Completion => Client.Completion;
    internal ManagedProcessProtocolClient Client { get; }

    internal void StartSupervision() => _ = SuperviseAsync();

    public ValueTask StopAsync(CancellationToken cancellationToken = default) => new(WaitForStopAsync(cancellationToken));
    public ValueTask DisposeAsync() => StopAsync();

    private async Task WaitForStopAsync(CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_stopGate)
        {
            _stopTask ??= StopCoreAsync();
            stopTask = _stopTask;
        }
        await stopTask.WaitAsync(cancellationToken);
    }

    private async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        _runtime.ManagedProcessDraining(this);
        await _protocolActivation.StopAsync(CancellationToken.None);
        var result = Client.DrainResult;
        if (result.Failure is not null)
            _runtime.ManagedProcessFailed(this, result.Failure);
        else
            _runtime.ManagedProcessStopped(this, result);
    }

    private async Task SuperviseAsync()
    {
        var result = await Client.ExitCompletion;
        if (Volatile.Read(ref _stopRequested) != 0)
            return;
        try
        {
            await _protocolActivation.StopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "ManagedProcess Collector 意外退出后释放 writer 失败");
        }
        _runtime.ManagedProcessFailed(this, result);
    }
}

internal sealed record ManagedProcessDrainResult(
    int? PendingFacts,
    int? PendingGaps,
    bool ProcessTerminated,
    ManagedProcessExit? Failure = null);
internal sealed record ManagedProcessExit(int? ExitCode, Exception? ProtocolError);

internal sealed class ManagedProcessProtocolException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class ManagedProcessExitedException(string message, int? exitCode = null)
    : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
}

internal interface ICollectorProtocolBinding
{
    string ExecutionDriver { get; }
}

internal sealed class ManagedProcessProtocolClient : IInProcessCollector, ICollectorProtocolBinding
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Process _process;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly ManagedProcessActivationOptions _options;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TaskCompletionSource<ManagedProcessExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ManagedProcessDrainResult> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stopGate = new();
    private Task? _stopTask;
    private InProcessCollectorActivation? _activation;
    private IReadOnlyDictionary<string, int> _selectedCapabilities = ImmutableDictionary<string, int>.Empty;
    private long _specRevision;
    private Guid? _drainMessageId;
    private ManagedProcessDrainResult? _drainResult;

    private ManagedProcessProtocolClient(
        Process process,
        ManagedProcessActivationOptions options,
        Guid helloMessageId,
        string artifactId,
        ProtocolSupport protocolSupport)
    {
        _process = process;
        _reader = process.StandardOutput;
        _writer = process.StandardInput;
        _options = options;
        HelloMessageId = helloMessageId;
        ArtifactId = artifactId;
        ProtocolSupport = protocolSupport;
    }

    public Guid HelloMessageId { get; }
    public string ExecutionDriver => "managedProcess";
    public Guid? ActivationId { get; private set; }
    public string ArtifactId { get; }
    public ProtocolSupport ProtocolSupport { get; }
    public int ProcessId => _process.Id;
    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
    public bool WasTerminated { get; private set; }
    public int? PendingFacts { get; private set; }
    public int? PendingGaps { get; private set; }
    public Task Completion => _exit.Task;
    public Task<ManagedProcessExit> ExitCompletion => _exit.Task;
    public ManagedProcessDrainResult DrainResult => _drainResult ?? (_drained.Task.IsCompletedSuccessfully
        ? _drained.Task.Result
        : new ManagedProcessDrainResult(PendingFacts, PendingGaps, WasTerminated));

    public void SetSelectedCapabilities(IReadOnlyDictionary<string, int> selectedCapabilities) =>
        _selectedCapabilities = selectedCapabilities;

    public static async Task<ManagedProcessProtocolClient> StartAsync(
        LocalCollectorPackage package,
        VerifiedCollectorArtifact artifact,
        Guid collectorInstanceId,
        ManagedProcessActivationOptions options,
        CancellationToken cancellationToken)
    {
        var artifactPath = Path.GetFullPath(Path.Combine(
            package.PackageDirectory,
            artifact.Entrypoint.Replace('/', Path.DirectorySeparatorChar)));
        var content = await File.ReadAllBytesAsync(artifactPath, cancellationToken);
        var actualHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
        if (actualHash != artifact.ContentHash || !content.AsSpan().SequenceEqual(artifact.Content.Span))
            throw new ManagedProcessProtocolException("ManagedProcess Artifact changed after Package verification.");

        var startInfo = new ProcessStartInfo
        {
            FileName = artifactPath,
            WorkingDirectory = package.PackageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["HEARTBEAT_COLLECTOR_INSTANCE_ID"] = collectorInstanceId.ToString("D");
        startInfo.Environment["HEARTBEAT_COLLECTOR_PACKAGE_ID"] = package.Manifest.PackageId;
        startInfo.Environment["HEARTBEAT_COLLECTOR_PACKAGE_VERSION"] = package.Manifest.Version;
        startInfo.Environment["HEARTBEAT_COLLECTOR_ARTIFACT_ID"] = artifact.ArtifactId;
        startInfo.Environment["HEARTBEAT_COLLECTOR_ARTIFACT_HASH"] = artifact.ContentHash;
        foreach (var pair in options.EnvironmentVariables)
        {
            if (pair.Key.StartsWith("HEARTBEAT_COLLECTOR_", StringComparison.Ordinal))
                throw new ArgumentException($"ManagedProcess environment variable '{pair.Key}' is reserved by the binding.");
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false.");
        }
        catch
        {
            process.Dispose();
            throw;
        }
        _ = DrainStandardErrorAsync(process.StandardError);

        try
        {
            using var hello = await ReadRequiredMessageAsync(process.StandardOutput, cancellationToken);
            RequireEnvelope(hello.RootElement, "heartbeat.collector.bootstrap/1", "activation.hello");
            var messageId = ReadUuidV7(hello.RootElement, "messageId");
            var body = RequireObject(hello.RootElement, "body");
            if (ReadGuid(body, "collectorInstanceId") != collectorInstanceId)
                throw new ManagedProcessProtocolException("activation.hello collectorInstanceId does not match the configured Instance.");
            var runtimeArtifact = RequireObject(body, "runtimeArtifact");
            if (ReadString(runtimeArtifact, "packageId") != package.Manifest.PackageId ||
                ReadString(runtimeArtifact, "packageVersion") != package.Manifest.Version ||
                ReadString(runtimeArtifact, "artifactId") != artifact.ArtifactId ||
                ReadString(runtimeArtifact, "artifactHash") != artifact.ContentHash)
                throw new ManagedProcessProtocolException("activation.hello runtimeArtifact does not match the selected verified Artifact.");
            var support = new ProtocolSupport(
                ReadPositiveIntArray(body, "protocolMajors"),
                ReadCapabilities(body, "supportedCapabilities"));
            return new ManagedProcessProtocolClient(process, options, messageId, artifact.ArtifactId, support);
        }
        catch (ManagedProcessExitedException exception)
        {
            await WaitForExitAsync(process);
            var exitCode = process.HasExited ? process.ExitCode : exception.ExitCode;
            process.Dispose();
            throw new ManagedProcessExitedException(
                "ManagedProcess Collector exited before activation.hello completed.",
                exitCode);
        }
        catch
        {
            Kill(process);
            process.Dispose();
            throw;
        }
    }

    public async ValueTask<InProcessCollectorInitialization> InitializeAsync(
        CollectorInitialization initialization,
        CancellationToken cancellationToken)
    {
        ActivationId = initialization.ActivationId;
        await WriteAsync(new
        {
            protocol = "heartbeat.collector.bootstrap/1",
            type = "activation.accepted",
            messageId = Guid.CreateVersion7(),
            replyTo = HelloMessageId,
            body = new
            {
                activationId = initialization.ActivationId,
                selectedProtocolMajor = 1,
                selectedCapabilities = _selectedCapabilities
            }
        }, cancellationToken);

        var initializeMessageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.initialize",
            messageId = initializeMessageId,
            activationId = initialization.ActivationId,
            body = new
            {
                instance = new
                {
                    collectorInstanceId = initialization.Instance.CollectorInstanceId,
                    subject = new
                    {
                        subjectId = initialization.Instance.Subject.SubjectId,
                        kind = SubjectKindName(initialization.Instance.Subject.Kind)
                    }
                },
                spec = new
                {
                    revision = initialization.Spec.SpecRevision,
                    config = new { schemaVersion = initialization.Spec.ConfigSchemaVersion, value = initialization.Spec.Config }
                },
                limits = initialization.Limits,
                hubTime = ProtocolTimestamp(DateTimeOffset.UtcNow)
            }
        }, cancellationToken);

        using var initialized = await ReadRequiredMessageAsync(_reader, cancellationToken);
        RequireResponse(initialized.RootElement, "activation.initialized", initializeMessageId, initialization.ActivationId);
        var appliedSpecRevision = ReadPositiveLong(RequireObject(initialized.RootElement, "body"), "appliedSpecRevision");
        _specRevision = initialization.Spec.SpecRevision;

        using var open = await ReadRequiredMessageAsync(_reader, cancellationToken);
        RequireEnvelope(open.RootElement, "heartbeat.collector/1", "streams.open", initialization.ActivationId);
        var openBody = RequireObject(open.RootElement, "body");
        if (ReadPositiveLong(openBody, "specRevision") != appliedSpecRevision)
            throw new ManagedProcessProtocolException("streams.open specRevision does not match activation.initialized.");
        var bindings = RequireArray(openBody, "bindings").EnumerateArray().Select(binding =>
        {
            var dimensions = RequireObject(binding, "dimensions").EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : throw new ManagedProcessProtocolException("Stream dimension values must be strings."),
                StringComparer.Ordinal);
            return new OutputBinding(ReadString(binding, "bindingId"), ReadString(binding, "outputId"), dimensions);
        }).ToArray();
        OpenMessageId = ReadUuidV7(open.RootElement, "messageId");
        return new InProcessCollectorInitialization(appliedSpecRevision, bindings);
    }

    private Guid OpenMessageId { get; set; }

    public async ValueTask OnStreamsOpenedAsync(InProcessCollectorStreamsOpened opened, CancellationToken cancellationToken)
    {
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "streams.opened",
            messageId = Guid.CreateVersion7(),
            activationId = opened.ActivationId,
            replyTo = OpenMessageId,
            body = new
            {
                streams = opened.Streams.Select(pair => new { bindingId = pair.Key, stream = StreamDescriptor(pair.Value) })
            }
        }, cancellationToken);

        using var ready = await ReadRequiredMessageAsync(_reader, cancellationToken);
        RequireEnvelope(ready.RootElement, "heartbeat.collector/1", "activation.ready", opened.ActivationId);
        var readyMessageId = ReadUuidV7(ready.RootElement, "messageId");
        var appliedSpecRevision = ReadPositiveLong(RequireObject(ready.RootElement, "body"), "appliedSpecRevision");
        if (appliedSpecRevision != _specRevision)
            throw new ManagedProcessProtocolException("activation.ready appliedSpecRevision does not match the initialized Spec.");
        _activation = await opened.ReadyAsync(cancellationToken);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.readyAck",
            messageId = Guid.CreateVersion7(),
            activationId = opened.ActivationId,
            replyTo = readyMessageId,
            body = new { appliedSpecRevision }
        }, cancellationToken);
        _ = PumpAsync();
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_stopGate)
        {
            _stopTask ??= StopCoreAsync();
            stopTask = _stopTask;
        }
        return new ValueTask(stopTask.WaitAsync(cancellationToken));
    }

    public async Task AbortAsync()
    {
        if (!_process.HasExited)
        {
            WasTerminated = true;
            Kill(_process);
        }
        await WaitForExitAsync(_process);
        _exit.TrySetResult(new ManagedProcessExit(ExitCode, null));
    }

    private async Task StopCoreAsync()
    {
        if (_process.HasExited)
        {
            var exited = await _exit.Task;
            _drainResult = new ManagedProcessDrainResult(
                PendingFacts,
                PendingGaps,
                false,
                exited);
            _drained.TrySetResult(_drainResult);
            return;
        }
        if (_activation is null)
        {
            WasTerminated = true;
            Kill(_process);
            await WaitForExitAsync(_process);
            _drainResult = new ManagedProcessDrainResult(null, null, true);
            _drained.TrySetResult(_drainResult);
            return;
        }

        var deadline = DateTimeOffset.UtcNow + _options.DrainGracePeriod;
        _drainMessageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.drain",
            messageId = _drainMessageId,
            activationId = ActivationId,
            body = new { deadline = ProtocolTimestamp(deadline) }
        }, CancellationToken.None);

        var drainAcknowledged = false;
        try
        {
            var drain = await _drained.Task.WaitAsync(_options.DrainGracePeriod);
            drainAcknowledged = drain.Failure is null;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
                await WaitForExitAsync(_process).WaitAsync(remaining);
        }
        catch (TimeoutException)
        {
            if (!_process.HasExited)
            {
                WasTerminated = true;
                Kill(_process);
            }
        }
        await WaitForExitAsync(_process);
        var exit = await _exit.Task;
        var failure = exit.ProtocolError is not null ||
                      (!drainAcknowledged && !WasTerminated) ||
                      (!WasTerminated && exit.ExitCode is not null and not 0)
            ? exit
            : null;
        _drainResult = new ManagedProcessDrainResult(
            PendingFacts,
            PendingGaps,
            WasTerminated,
            failure);
        _drained.TrySetResult(_drainResult);
    }

    private async Task PumpAsync()
    {
        Exception? protocolError = null;
        try
        {
            while (await ReadOptionalMessageAsync(_reader, CancellationToken.None) is { } message)
            {
                using (message)
                {
                    var root = message.RootElement;
                    switch (ReadString(root, "type"))
                    {
                        case "facts.publish":
                            await HandleFactsPublishAsync(root);
                            break;
                        case "stream.gap":
                            await HandleStreamGapAsync(root);
                            break;
                        case "activation.drained":
                            HandleDrained(root);
                            break;
                        default:
                            throw new ManagedProcessProtocolException($"ManagedProcess sent unexpected message type '{ReadString(root, "type")}'.");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            protocolError = exception is ManagedProcessProtocolException
                ? exception
                : new ManagedProcessProtocolException("ManagedProcess protocol stream is invalid.", exception);
            if (!_process.HasExited)
            {
                WasTerminated = true;
                Kill(_process);
            }
        }
        finally
        {
            await WaitForExitAsync(_process);
            var exit = new ManagedProcessExit(ExitCode, protocolError);
            _exit.TrySetResult(exit);
            if (_drainMessageId is not null && !_drained.Task.IsCompleted)
            {
                var failure = protocolError is not null || !WasTerminated
                    ? exit
                    : null;
                _drainResult = new ManagedProcessDrainResult(
                    PendingFacts,
                    PendingGaps,
                    WasTerminated,
                    failure);
                _drained.TrySetResult(_drainResult);
            }
        }
    }

    private async Task HandleFactsPublishAsync(JsonElement root)
    {
        RequireEnvelope(root, "heartbeat.collector/1", "facts.publish", ActivationId!.Value);
        var messageId = ReadUuidV7(root, "messageId");
        var facts = RequireArray(RequireObject(root, "body"), "facts").EnumerateArray().Select(ReadFact).ToArray();
        var streamIds = facts.Select(fact => fact.StreamId).Distinct().ToArray();
        if (streamIds.Length != 1 || _activation is null ||
            _activation.Streams.Values.All(stream => stream.Descriptor.StreamId != streamIds[0]))
            throw new ManagedProcessProtocolException("facts.publish references an unopened Stream.");
        var stream = _activation.Streams.Values.Single(item => item.Descriptor.StreamId == streamIds[0]);
        var acknowledgement = await stream.PublishAsync(messageId, facts);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = acknowledgement.IsMessageRejected ? "facts.rejected" : "facts.ack",
            messageId = Guid.CreateVersion7(),
            activationId = ActivationId,
            replyTo = messageId,
            body = acknowledgement.IsMessageRejected
                ? (object)new { error = acknowledgement.MessageError }
                : new
                {
                    results = acknowledgement.Results.Select(result => new
                    {
                        index = result.Index,
                        status = EnumName(result.Status),
                        error = result.Error,
                        retryAfterMs = result.RetryAfterMilliseconds
                    })
                }
        }, CancellationToken.None);
    }

    private async Task HandleStreamGapAsync(JsonElement root)
    {
        RequireEnvelope(root, "heartbeat.collector/1", "stream.gap", ActivationId!.Value);
        var messageId = ReadUuidV7(root, "messageId");
        var body = RequireObject(root, "body");
        var streamId = ReadGuid(body, "streamId");
        var time = RequireObject(body, "factTime");
        var gap = new StreamGapReport(
            ReadUtcTimestamp(time, "start"), ReadUtcTimestamp(time, "end"), ReadString(body, "reason"),
            body.TryGetProperty("estimatedFactsLost", out var estimate) ? estimate.GetInt32() : null);
        var stream = _activation?.Streams.Values.SingleOrDefault(item => item.Descriptor.StreamId == streamId)
            ?? throw new ManagedProcessProtocolException("stream.gap references an unopened Stream.");
        var outcome = await stream.ReportGapAsync(messageId, gap);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = outcome.IsAcknowledged ? "stream.gapAck" : "stream.gapRejected",
            messageId = Guid.CreateVersion7(),
            activationId = ActivationId,
            replyTo = messageId,
            body = outcome.IsAcknowledged ? (object)new { streamId } : new { error = outcome.Error }
        }, CancellationToken.None);
    }

    private void HandleDrained(JsonElement root)
    {
        RequireEnvelope(root, "heartbeat.collector/1", "activation.drained", ActivationId!.Value);
        var body = RequireObject(root, "body");
        if (_drainMessageId is null || ReadGuid(root, "replyTo") != _drainMessageId)
            throw new ManagedProcessProtocolException("activation.drained replyTo does not match activation.drain.");
        if (ReadPositiveLong(body, "appliedSpecRevision") != _specRevision)
            throw new ManagedProcessProtocolException("activation.drained appliedSpecRevision does not match the initialized Spec.");
        PendingFacts = ReadNonNegativeInt(body, "pendingFacts");
        PendingGaps = ReadNonNegativeInt(body, "pendingGaps");
        _drainResult = new ManagedProcessDrainResult(PendingFacts, PendingGaps, false);
        _drained.TrySetResult(_drainResult);
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, SerializerOptions);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new ManagedProcessProtocolException("ManagedProcess protocol connection closed while writing.", exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<JsonDocument> ReadRequiredMessageAsync(StreamReader reader, CancellationToken cancellationToken) =>
        await ReadOptionalMessageAsync(reader, cancellationToken)
        ?? throw new ManagedProcessExitedException("ManagedProcess protocol connection closed unexpectedly.");

    private static async Task<JsonDocument?> ReadOptionalMessageAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
            return null;
        if (line.Length > 1_048_576)
            throw new ManagedProcessProtocolException("ManagedProcess protocol message exceeds 1 MiB.");
        try
        {
            var document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            RejectDuplicateKeys(document.RootElement);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ManagedProcessProtocolException("ManagedProcess emitted malformed JSON.", exception);
        }
    }

    private static void RequireResponse(JsonElement root, string type, Guid replyTo, Guid activationId)
    {
        RequireEnvelope(root, "heartbeat.collector/1", type, activationId);
        if (ReadGuid(root, "replyTo") != replyTo)
            throw new ManagedProcessProtocolException($"{type} replyTo does not match its request.");
    }

    private static void RequireEnvelope(JsonElement root, string protocol, string type, Guid? activationId = null)
    {
        if (root.ValueKind != JsonValueKind.Object || ReadString(root, "protocol") != protocol || ReadString(root, "type") != type)
            throw new ManagedProcessProtocolException($"Expected {type} protocol envelope.");
        _ = ReadUuidV7(root, "messageId");
        if (activationId is not null && ReadGuid(root, "activationId") != activationId)
            throw new ManagedProcessProtocolException($"{type} activationId does not match the live Activation.");
        _ = RequireObject(root, "body");
    }

    private static object StreamDescriptor(FactStreamDescriptor descriptor) => new
    {
        streamId = descriptor.StreamId,
        collectorInstanceId = descriptor.CollectorInstanceId,
        subject = new { subjectId = descriptor.Subject.SubjectId, kind = SubjectKindName(descriptor.Subject.Kind) },
        outputId = descriptor.OutputId,
        source = descriptor.Source,
        factKind = EnumName(descriptor.FactKind),
        schema = new { id = descriptor.Schema.Id, major = descriptor.Schema.Major, revision = descriptor.Schema.Revision, hash = descriptor.Schema.Hash },
        dimensions = descriptor.Dimensions
    };

    private static FactSubmission ReadFact(JsonElement fact)
    {
        var recordState = ReadString(fact, "recordState") switch
        {
            "present" => FactRecordState.Present,
            "retracted" => FactRecordState.Retracted,
            var value => throw new ManagedProcessProtocolException($"Unknown Fact recordState '{value}'.")
        };
        var time = RequireObject(fact, "time");
        return new FactSubmission(
            ReadGuid(fact, "streamId"), ReadPositiveInt(fact, "schemaRevision"), ReadUuidV7(fact, "factId"),
            ReadPositiveLong(fact, "revision"),
            fact.TryGetProperty("observedAt", out _) ? ReadUtcTimestamp(fact, "observedAt") : null,
            recordState,
            new SegmentFactTime(ReadUtcTimestamp(time, "start"), ReadUtcTimestamp(time, "end"), ReadBoolean(time, "isFinal")),
            recordState == FactRecordState.Present ? fact.GetProperty("payload").Clone() : default);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> ReadCapabilities(JsonElement parent, string name) =>
        RequireObject(parent, name).EnumerateObject().ToDictionary(
            property => property.Name,
            property => (IReadOnlyList<int>)property.Value.EnumerateArray().Select(value => value.GetInt32()).ToArray(),
            StringComparer.Ordinal);

    private static IReadOnlyList<int> ReadPositiveIntArray(JsonElement parent, string name)
    {
        var values = RequireArray(parent, name).EnumerateArray().Select(value => value.GetInt32()).ToArray();
        if (values.Length == 0 || values.Any(value => value <= 0))
            throw new ManagedProcessProtocolException($"{name} must contain positive integers.");
        return values;
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new ManagedProcessProtocolException($"{name} must be an object.");
        return value;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ManagedProcessProtocolException($"{name} must be an array.");
        return value;
    }

    private static string ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ManagedProcessProtocolException($"{name} must be a non-empty string.");
        return value.GetString()!;
    }

    private static Guid ReadGuid(JsonElement parent, string name) =>
        Guid.TryParseExact(ReadString(parent, name), "D", out var value) && value != Guid.Empty
            ? value
            : throw new ManagedProcessProtocolException($"{name} must be a canonical UUID.");

    private static Guid ReadUuidV7(JsonElement parent, string name)
    {
        var value = ReadGuid(parent, name);
        return value.Version == 7 ? value : throw new ManagedProcessProtocolException($"{name} must be a UUIDv7.");
    }

    private static int ReadPositiveInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result) || result <= 0)
            throw new ManagedProcessProtocolException($"{name} must be a positive integer.");
        return result;
    }

    private static long ReadPositiveLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt64(out var result) || result <= 0)
            throw new ManagedProcessProtocolException($"{name} must be a positive integer.");
        return result;
    }

    private static int ReadNonNegativeInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result) || result < 0)
            throw new ManagedProcessProtocolException($"{name} must be a non-negative integer.");
        return result;
    }

    private static bool ReadBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ManagedProcessProtocolException($"{name} must be a boolean.");
        return value.GetBoolean();
    }

    private static DateTimeOffset ReadUtcTimestamp(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text || !text.EndsWith('Z') ||
            !value.TryGetDateTimeOffset(out var result) || result.Offset != TimeSpan.Zero)
            throw new ManagedProcessProtocolException($"{name} must be an RFC 3339 UTC timestamp.");
        return result;
    }

    private static string ProtocolTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ManagedProcessProtocolException($"Duplicate JSON field '{property.Name}'.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateKeys(item);
        }
    }

    private static string SubjectKindName(SubjectKind kind) => kind switch
    {
        SubjectKind.Machine => "machine",
        SubjectKind.Account => "account",
        SubjectKind.Person => "person",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string EnumName<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static async Task DrainStandardErrorAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                Log.Debug("ManagedProcess stderr: {Line}", line);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static Task WaitForExitAsync(Process process) => process.HasExited ? Task.CompletedTask : process.WaitForExitAsync();

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
