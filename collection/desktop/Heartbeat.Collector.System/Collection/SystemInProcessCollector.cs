using Heartbeat.Collection.CollectorProtocol;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using ClientError = Heartbeat.Collection.CollectorProtocol.CollectorProtocolError;
using ClientAuthorizationField = Heartbeat.Collection.CollectorProtocol.CollectorAuthorizationField;
using HubError = Heartbeat.Collection.Hub.Collectors.Protocol.CollectorProtocolError;

namespace Heartbeat.Collector.System.Collection;

/// <summary>
/// InProcess adapter. It translates Hub typed callbacks into the same Collector-side client
/// interface used by the stdio adapter; it does not own protocol lifecycle or delivery rules.
/// </summary>
public sealed class SystemInProcessCollector(
    SystemCollectorProtocolAdapter protocol,
    AppMonitorService monitor) :
    IInProcessCollector,
    IInProcessCollectorDeadlineFence,
    ICollectorProtocolBinding,
    ICollectorProtocolApplication,
    IDisposable
{
    public const string PackageId = "heartbeat.collector.system";
    public const string ForegroundBindingId = "foreground";
    public const string ForegroundOutputId = "foreground";
    public const string InputEventBindingId = "input-events";
    public const string InputEventOutputId = "input-events";

    private readonly CollectorClientDefinition _definition = new(
        "system.inprocess",
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["facts.segment"] = [1],
            ["facts.event"] = [1],
            ["diagnostics.stream-gap"] = [1]
        },
        "machine",
        [
            new CollectorOutputBinding(
                ForegroundBindingId,
                ForegroundOutputId,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            new CollectorOutputBinding(
                InputEventBindingId,
                InputEventOutputId,
                new Dictionary<string, string>(StringComparer.Ordinal))
        ],
        OutboxCapacity: 100_000,
        Diagnostics: protocol);
    private readonly TaskCompletionSource<CollectorClientInitialization> _initialization = NewSource<CollectorClientInitialization>();
    private readonly TaskCompletionSource<IReadOnlyList<CollectorOutputBinding>> _requestedOutputs = NewSource<IReadOnlyList<CollectorOutputBinding>>();
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, CollectorClientStream>> _openedStreams = NewSource<IReadOnlyDictionary<string, CollectorClientStream>>();
    private readonly TaskCompletionSource _readyRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _readyCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _applicationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CollectorDrainRequest> _drainRequested = NewSource<CollectorDrainRequest>();
    private readonly TaskCompletionSource<CollectorDrainResult> _drainCompleted = NewSource<CollectorDrainResult>();
    private readonly CancellationTokenSource _clientLifetime = new();
    private readonly object _startGate = new();
    private CollectorProtocolClient? _client;
    private Task<CollectorDrainExecutionResult>? _clientRun;
    private InProcessCollectorActivation? _liveActivation;
    private ICollectorDurableCommitFence? _durableCommitFence;

    public string ArtifactId => _definition.ArtifactId;

    public ProtocolSupport ProtocolSupport { get; } = new(
        [1],
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["facts.segment"] = [1],
            ["facts.event"] = [1],
            ["diagnostics.stream-gap"] = [1]
        });

    public async ValueTask<InProcessCollectorInitialization> InitializeAsync(
        CollectorInitialization initialization,
        CancellationToken cancellationToken)
    {
        if (initialization.Instance.PackageId != PackageId)
            throw new InvalidOperationException(
                $"The system Collector cannot activate Package '{initialization.Instance.PackageId}'.");
        var durableCommitFence = initialization.Resources.DurableCommitFence
            ?? throw new InvalidOperationException(
                "Hub did not provide the system Collector durable commit fence.");
        _durableCommitFence = durableCommitFence;
        protocol.AttachDurableIngressFence(durableCommitFence);
        StartClient();
        _initialization.TrySetResult(new CollectorClientInitialization(
            initialization.ActivationId,
            initialization.Instance.CollectorInstanceId,
            initialization.Instance.Subject.SubjectId,
            SubjectKindName(initialization.Instance.Subject.Kind),
            initialization.Spec.SpecRevision,
            initialization.Spec.ConfigVersion,
            initialization.Spec.Config.Clone(),
            initialization.Limits.MaxFactsPerBatch,
            initialization.Limits.MaxBatchBytes,
            initialization.Resources.DataDirectory
                ?? throw new InvalidOperationException("Hub did not provide the system Collector data directory."),
            _definition.Capabilities.ToDictionary(pair => pair.Key, pair => pair.Value.Max(), StringComparer.Ordinal)));
        var requested = await _requestedOutputs.Task.WaitAsync(cancellationToken);
        return new InProcessCollectorInitialization(
            initialization.Spec.SpecRevision,
            requested.Select(output => new OutputBinding(
                output.BindingId,
                output.OutputId,
                output.Dimensions)).ToArray());
    }

    public async ValueTask OnStreamsOpenedAsync(
        InProcessCollectorStreamsOpened opened,
        CancellationToken cancellationToken)
    {
        _openedStreams.TrySetResult(opened.Streams.ToDictionary(
            pair => pair.Key,
            pair => ToClientStream(pair.Key, pair.Value),
            StringComparer.Ordinal));
        await _readyRequested.Task.WaitAsync(cancellationToken);
        _liveActivation = await opened.ReadyAsync(cancellationToken);
        _readyCompleted.TrySetResult();
        await _applicationStarted.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask<InProcessCollectorDrainResult> StopAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        using var deadlineFence = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            _clientLifetime);
        _drainRequested.TrySetResult(new CollectorDrainRequest(
            Guid.CreateVersion7(),
            deadline));
        await _drainCompleted.Task.WaitAsync(cancellationToken);
        var execution = _clientRun is null
            ? throw new InvalidOperationException("The system Collector Protocol client is not running.")
            : await _clientRun.WaitAsync(cancellationToken);
        return new InProcessCollectorDrainResult(
            new InProcessCollectorLogicalDrainResult(
                execution.PendingFacts,
                execution.PendingGaps,
                Enum.Parse<CollectorDrainReason>(execution.LogicalResult.Reason.ToString()),
                execution.LogicalResult.RemainderDurable),
            Enum.Parse<Heartbeat.Collection.Hub.Collectors.Protocol.CollectorDrainCompletionReason>(
                execution.CompletionReason.ToString()),
            execution.CompletionError);
    }

    void IInProcessCollectorDeadlineFence.FenceAfterDeadline()
    {
        _clientLifetime.Cancel();
    }

    ValueTask<CollectorClientInitialization> ICollectorProtocolBinding.StartAsync(
        CollectorClientDefinition definition,
        CancellationToken cancellationToken) =>
        new(_initialization.Task.WaitAsync(cancellationToken));

    async ValueTask<IReadOnlyDictionary<string, CollectorClientStream>> ICollectorProtocolBinding.OpenStreamsAsync(
        long specRevision,
        IReadOnlyList<CollectorOutputBinding> outputs,
        CancellationToken cancellationToken)
    {
        _requestedOutputs.TrySetResult(outputs);
        return await _openedStreams.Task.WaitAsync(cancellationToken);
    }

    async ValueTask ICollectorProtocolBinding.ReadyAsync(
        long appliedSpecRevision,
        CancellationToken cancellationToken)
    {
        _readyRequested.TrySetResult();
        await _readyCompleted.Task.WaitAsync(cancellationToken);
    }

    async ValueTask<CollectorFactBatchAcknowledgement> ICollectorProtocolBinding.PublishAsync(
        Guid messageId,
        IReadOnlyList<BoundCollectorFact> facts,
        CancellationToken cancellationToken)
    {
        var activation = _liveActivation ?? throw new InvalidOperationException(
            "The system Collector cannot publish before Ready.");
        if (facts.Count == 0 || facts.Select(fact => fact.StreamId).Distinct().Count() != 1)
            throw new ArgumentException("An InProcess publish must target exactly one Stream.", nameof(facts));
        var stream = activation.Streams.Values.Single(item => item.Descriptor.StreamId == facts[0].StreamId);
        var acknowledgement = await stream.PublishAsync(
            messageId,
            facts.Select(ToHubFact).ToArray(),
            cancellationToken);
        return new CollectorFactBatchAcknowledgement(
            acknowledgement.Results.Select(outcome => new CollectorFactDeliveryOutcome(
                outcome.Index,
                Enum.Parse<CollectorFactDeliveryStatus>(outcome.Status.ToString()),
                ToClientError(outcome.Error),
                outcome.RetryAfterMilliseconds)).ToArray(),
            ToClientError(acknowledgement.MessageError));
    }

    async ValueTask<CollectorGapDeliveryOutcome> ICollectorProtocolBinding.ReportGapAsync(
        Guid messageId,
        Guid streamId,
        CollectorStreamGap gap,
        CancellationToken cancellationToken)
    {
        var activation = _liveActivation ?? throw new InvalidOperationException(
            "The system Collector cannot report a Gap before Ready.");
        var stream = activation.Streams.Values.Single(item => item.Descriptor.StreamId == streamId);
        var outcome = await stream.ReportGapAsync(
            messageId,
            new StreamGapReport(gap.GapId, gap.Start, gap.End, gap.Reason, gap.EstimatedFactsLost),
            cancellationToken);
        return new CollectorGapDeliveryOutcome(
            Enum.Parse<CollectorGapDeliveryStatus>(outcome.Status.ToString()),
            ToClientError(outcome.Error),
            outcome.RetryAfterMilliseconds);
    }

    ValueTask<CollectorAuthorizationResponse> ICollectorProtocolBinding.ChallengeAsync(
        Guid interactionId,
        string kind,
        string title,
        string? message,
        IReadOnlyList<ClientAuthorizationField> fields,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<CollectorAuthorizationResponse>(
            new NotSupportedException("The system Collector does not use Interactive Authorization."));

    ValueTask ICollectorProtocolBinding.CompleteAuthorizationAsync(
        Guid interactionId,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("The system Collector does not use Interactive Authorization."));

    ValueTask<string?> ICollectorProtocolBinding.ReadSecretAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromException<string?>(new NotSupportedException("The system Collector does not use Collector Secrets."));

    ValueTask ICollectorProtocolBinding.WriteSecretAsync(string key, string value, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("The system Collector does not use Collector Secrets."));

    ValueTask ICollectorProtocolBinding.DeleteSecretAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("The system Collector does not use Collector Secrets."));

    ValueTask<CollectorDrainRequest> ICollectorProtocolBinding.WaitForDrainAsync(CancellationToken cancellationToken) =>
        new(_drainRequested.Task.WaitAsync(cancellationToken));

    ValueTask ICollectorProtocolBinding.CompleteDrainAsync(
        CollectorDrainResult result,
        CancellationToken cancellationToken)
    {
        _drainCompleted.TrySetResult(result);
        return ValueTask.CompletedTask;
    }

    bool ICollectorProtocolBinding.TryPublishDurableFile(
        string preparedPath,
        string authoritativePath)
    {
        var durableCommitFence = _durableCommitFence ?? throw new InvalidOperationException(
            "The system Collector durable commit fence is unavailable before initialization.");
        return durableCommitFence.TryPublishFile(preparedPath, authoritativePath);
    }

    void ICollectorProtocolBinding.ThrowIfAcknowledgementSuperseded()
    {
        if (_liveActivation?.State == CollectorActivationState.Stopped)
        {
            throw new OperationCanceledException(
                "InProcess acknowledgement was superseded by the Hub drain deadline fence.");
        }
    }

    bool ICollectorProtocolBinding.TryCommitAcknowledgement(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var activation = _liveActivation ?? throw new InvalidOperationException(
            "The system Collector cannot commit an acknowledgement before Ready.");
        return activation.TryCommitAcknowledgement(commit);
    }

    ValueTask ICollectorProtocolApplication.InitializeAsync(
        CollectorActivation activation,
        CancellationToken cancellationToken)
    {
        protocol.Attach(activation);
        return ValueTask.CompletedTask;
    }

    async ValueTask ICollectorProtocolApplication.StartAsync(
        CollectorActivation activation,
        CancellationToken cancellationToken)
    {
        try
        {
            protocol.Start();
            await monitor.StartAsync(cancellationToken);
            _applicationStarted.TrySetResult();
        }
        catch (Exception exception)
        {
            _applicationStarted.TrySetException(exception);
            throw;
        }
    }

    async ValueTask ICollectorProtocolApplication.StopAsync(
        CollectorActivation activation,
        CancellationToken cancellationToken)
    {
        var monitorStop = monitor.StopAsync(cancellationToken);
        try
        {
            await protocol.PrepareDrainAsync(cancellationToken);
            await monitorStop.WaitAsync(cancellationToken);
        }
        finally
        {
            await protocol.CompleteDrainAsync(cancellationToken);
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        // The binding owns no resources independently of the host-managed monitor and protocol.
        // Supporting synchronous disposal keeps this adapter compatible with synchronous DI scopes.
    }

    private void StartClient()
    {
        lock (_startGate)
        {
            if (_clientRun is not null)
                return;
            _client = new CollectorProtocolClient(_definition, this);
            _clientRun = _client.RunAsync(this, _clientLifetime.Token);
            _ = _clientRun.ContinueWith(
                task =>
                {
                    if (task.Exception is not { } exception)
                        return;
                    _requestedOutputs.TrySetException(exception.InnerException ?? exception);
                    _openedStreams.TrySetException(exception.InnerException ?? exception);
                    _readyCompleted.TrySetException(exception.InnerException ?? exception);
                    _applicationStarted.TrySetException(exception.InnerException ?? exception);
                    _drainCompleted.TrySetException(exception.InnerException ?? exception);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    private static CollectorClientStream ToClientStream(
        string bindingId,
        FactStreamDescriptor descriptor) => new(
        bindingId,
        descriptor.StreamId,
        descriptor.CollectorInstanceId,
        descriptor.Subject.SubjectId,
        SubjectKindName(descriptor.Subject.Kind),
        descriptor.OutputId,
        descriptor.Source,
        descriptor.FactKind.ToString().ToLowerInvariant(),
        descriptor.Schema.Id,
        descriptor.Schema.Major,
        descriptor.Schema.Revision,
        descriptor.Schema.Hash,
        descriptor.Dimensions);

    private static FactSubmission ToHubFact(BoundCollectorFact fact) => new(
        fact.StreamId,
        fact.SchemaRevision,
        fact.FactId,
        fact.Revision,
        fact.ObservedAt,
        fact.RecordState == CollectorFactRecordState.Present
            ? FactRecordState.Present
            : FactRecordState.Retracted,
        fact.Time switch
        {
            CollectorSegmentFactTime segment => new SegmentFactTime(
                segment.Start,
                segment.End,
                segment.IsFinal),
            CollectorEventFactTime occurrence => new EventFactTime(occurrence.OccurredAt),
            _ => throw new InvalidOperationException("Unknown Collector Fact time shape.")
        },
        fact.Payload.Clone());

    private static ClientError? ToClientError(HubError? error) => error is null
        ? null
        : new ClientError(error.Code, error.Message, error.Retryable);

    private static string SubjectKindName(SubjectKind kind) => kind switch
    {
        SubjectKind.Machine => "machine",
        SubjectKind.Account => "account",
        SubjectKind.Person => "person",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static TaskCompletionSource<T> NewSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
