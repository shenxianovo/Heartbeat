namespace Heartbeat.Collection.CollectorProtocol;

/// <summary>
/// Collector-side protocol module. Callers provide observations through
/// <see cref="CollectorActivation"/>; this module owns lifecycle, durable delivery, ACK/retry,
/// Gap, authorization, Collector Secret, and drain semantics.
/// </summary>
public sealed class CollectorProtocolClient(
    CollectorClientDefinition definition,
    ICollectorProtocolBinding binding,
    Func<DateTimeOffset>? clock = null) : IAsyncDisposable
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private CollectorProtocolOutbox? _outbox;
    private CollectorActivation? _activation;

    public async Task<CollectorDrainResult> RunAsync(
        ICollectorProtocolApplication application,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ValidateDefinition();
        var initialization = await binding.StartAsync(definition, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                initialization.SubjectKind,
                definition.RequiredSubjectKind,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Collector requires a {definition.RequiredSubjectKind} Subject, not '{initialization.SubjectKind}'.");
        foreach (var capability in definition.RequiredCapabilities ?? definition.Capabilities.Keys.ToHashSet(StringComparer.Ordinal))
        {
            if (!initialization.SelectedCapabilities.ContainsKey(capability))
                throw new InvalidOperationException($"Hub did not select required capability '{capability}'.");
        }

        _outbox = CollectorProtocolOutbox.Open(
            initialization.DataDirectory,
            definition.OutboxCapacity,
            definition.Outputs,
            _clock());
        await PersistMutationAsync(_outbox.BeginActivation, cancellationToken).ConfigureAwait(false);
        ReportDiagnostics();
        _activation = new CollectorActivation(this, initialization);
        await application.InitializeAsync(_activation, cancellationToken).ConfigureAwait(false);

        var streams = await binding.OpenStreamsAsync(
            initialization.SpecRevision,
            definition.Outputs,
            cancellationToken).ConfigureAwait(false);
        _activation.SetStreams(streams);
        await binding.ReadyAsync(initialization.SpecRevision, cancellationToken).ConfigureAwait(false);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        var applicationTask = application.StartAsync(_activation, cancellationToken).AsTask();
        var drainTask = binding.WaitForDrainAsync(cancellationToken).AsTask();
        var first = await Task.WhenAny(applicationTask, drainTask).ConfigureAwait(false);
        CollectorDrainRequest drain;
        if (ReferenceEquals(first, applicationTask))
        {
            await applicationTask.ConfigureAwait(false);
            drain = await drainTask.ConfigureAwait(false);
        }
        else
        {
            drain = await drainTask.ConfigureAwait(false);
        }
        await application.StopAsync(_activation, CancellationToken.None).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource();
        var remaining = drain.Deadline - _clock();
        if (remaining > TimeSpan.Zero)
            deadline.CancelAfter(remaining);
        else
            deadline.Cancel();
        try
        {
            await FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Drain is best effort; the truthful pending counts below remain durable.
        }
        var result = new CollectorDrainResult(
            initialization.SpecRevision,
            _outbox.Facts.Count,
            _outbox.Gaps.Count);
        await binding.CompleteDrainAsync(result, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    internal IReadOnlyList<CollectorFact> PendingFacts =>
        _outbox?.Facts.Select(item => item.Fact).ToArray() ?? [];

    internal async ValueTask PublishAsync(CollectorFact fact, CancellationToken cancellationToken)
    {
        EnsureBinding(fact.BindingId);
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistMutationAsync(() => _outbox!.Enqueue(fact), cancellationToken).ConfigureAwait(false);
            await FlushLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    internal async ValueTask ReportGapAsync(CollectorStreamGap gap, CancellationToken cancellationToken)
    {
        EnsureBinding(gap.BindingId);
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistMutationAsync(() => _outbox!.EnqueueGap(gap), cancellationToken).ConfigureAwait(false);
            await FlushLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    internal ValueTask<CollectorAuthorizationResponse> ChallengeAsync(
        string kind,
        string title,
        string? message,
        IReadOnlyList<CollectorAuthorizationField> fields,
        CancellationToken cancellationToken) =>
        binding.ChallengeAsync(
            Guid.CreateVersion7(), kind, title, message, fields, cancellationToken);

    internal ValueTask CompleteAuthorizationAsync(Guid interactionId, CancellationToken cancellationToken) =>
        binding.CompleteAuthorizationAsync(interactionId, cancellationToken);

    internal ValueTask<string?> ReadSecretAsync(string key, CancellationToken cancellationToken) =>
        binding.ReadSecretAsync(key, cancellationToken);

    internal ValueTask WriteSecretAsync(string key, string value, CancellationToken cancellationToken) =>
        binding.WriteSecretAsync(key, value, cancellationToken);

    internal ValueTask DeleteSecretAsync(string key, CancellationToken cancellationToken) =>
        binding.DeleteSecretAsync(key, cancellationToken);

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private async Task FlushLockedAsync(CancellationToken cancellationToken)
    {
        if (_activation is null || !_activation.HasStreams)
            return;
        while (_outbox!.Facts.FirstOrDefault() is { } pending)
        {
            var stream = _activation.Stream(pending.Fact.BindingId);
            var fact = Bind(pending.Fact, stream);
            var acknowledgement = await binding.PublishAsync(
                pending.MessageId,
                [fact],
                cancellationToken).ConfigureAwait(false);
            if (acknowledgement.IsMessageRejected)
            {
                await PersistMutationAsync(
                    () => _outbox.DeadLetter(pending, acknowledgement.MessageError!, _clock()),
                    cancellationToken).ConfigureAwait(false);
                ReportDiagnostics();
                continue;
            }
            if (acknowledgement.Results.Count != 1)
                throw new InvalidDataException("Collector Protocol returned an invalid Fact ACK count.");
            var outcome = acknowledgement.Results[0];
            if (outcome.Status == CollectorFactDeliveryStatus.Retry)
            {
                await PersistMutationAsync(
                    () => _outbox.RetryFact(pending.MessageId),
                    cancellationToken).ConfigureAwait(false);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(outcome.RetryAfterMilliseconds ?? 1_000),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (outcome.IsAcknowledged)
            {
                await PersistMutationAsync(
                    () => _outbox.AcknowledgeFact(pending.MessageId),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            await PersistMutationAsync(
                () => _outbox.DeadLetter(
                    pending,
                    outcome.Error ?? new CollectorProtocolError(
                        "fact_delivery_failed",
                        $"Hub returned '{outcome.Status}' for a Collector Fact.",
                        false),
                    _clock()),
                cancellationToken).ConfigureAwait(false);
            ReportDiagnostics();
        }

        while (_outbox.Gaps.FirstOrDefault() is { } pending)
        {
            var stream = _activation.Stream(pending.Gap.BindingId);
            var outcome = await binding.ReportGapAsync(
                pending.MessageId,
                stream.StreamId,
                pending.Gap,
                cancellationToken).ConfigureAwait(false);
            if (outcome.IsAcknowledged)
            {
                await PersistMutationAsync(
                    () => _outbox.AcknowledgeGap(pending.MessageId),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (outcome.Status == CollectorGapDeliveryStatus.Retry || outcome.Error?.Retryable == true)
            {
                await PersistMutationAsync(
                    () => _outbox.RetryGap(pending.MessageId),
                    cancellationToken).ConfigureAwait(false);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(outcome.RetryAfterMilliseconds ?? 1_000),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            return;
        }
    }

    private async Task PersistMutationAsync(Action mutation, CancellationToken cancellationToken)
    {
        try
        {
            mutation();
            return;
        }
        catch (IOException)
        {
            // The outbox mutates memory before persisting. Keep that state and retry the same
            // persistence attempt without requiring another domain observation.
        }

        var delay = TimeSpan.FromMilliseconds(50);
        while (true)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            try
            {
                _outbox!.PersistPending();
                return;
            }
            catch (IOException)
            {
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1_000));
            }
        }
    }

    private void ReportDiagnostics() => definition.Diagnostics?.Report(new CollectorClientDiagnostic(
        _outbox!.DeadLetterCount,
        _outbox.DeadLetterPath));

    private void EnsureBinding(string bindingId)
    {
        if (definition.Outputs.All(output => output.BindingId != bindingId))
            throw new ArgumentException($"Collector output binding '{bindingId}' is not declared.", nameof(bindingId));
    }

    private void ValidateDefinition()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ArtifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.RequiredSubjectKind);
        if (definition.Outputs.Count == 0 ||
            definition.Outputs.Any(output => string.IsNullOrWhiteSpace(output.BindingId) ||
                                             string.IsNullOrWhiteSpace(output.OutputId)) ||
            definition.Outputs.GroupBy(output => output.BindingId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new ArgumentException("Collector outputs must have unique non-empty binding IDs.");
    }

    private static BoundCollectorFact Bind(CollectorFact fact, CollectorClientStream stream) => new(
        stream.StreamId,
        fact.SchemaRevision > 0 ? fact.SchemaRevision : stream.SchemaRevision,
        fact.FactId,
        fact.Revision,
        fact.ObservedAt,
        fact.RecordState,
        fact.Time,
        fact.Payload.Clone());

    public async ValueTask DisposeAsync()
    {
        _deliveryGate.Dispose();
        await binding.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class CollectorActivation
{
    private readonly CollectorProtocolClient _client;
    private IReadOnlyDictionary<string, CollectorClientStream>? _streams;

    internal CollectorActivation(
        CollectorProtocolClient client,
        CollectorClientInitialization initialization)
    {
        _client = client;
        Initialization = initialization;
    }

    public CollectorClientInitialization Initialization { get; }
    public IReadOnlyList<CollectorFact> PendingFacts => _client.PendingFacts;
    public IReadOnlyDictionary<string, CollectorClientStream> Streams =>
        _streams ?? throw new InvalidOperationException("Collector Fact Streams are not open yet.");
    internal bool HasStreams => _streams is not null;

    public ValueTask PublishAsync(CollectorFact fact, CancellationToken cancellationToken = default) =>
        _client.PublishAsync(fact, cancellationToken);

    public ValueTask ReportGapAsync(CollectorStreamGap gap, CancellationToken cancellationToken = default) =>
        _client.ReportGapAsync(gap, cancellationToken);

    public ValueTask<CollectorAuthorizationResponse> ChallengeAsync(
        string kind,
        string title,
        string? message,
        IReadOnlyList<CollectorAuthorizationField> fields,
        CancellationToken cancellationToken = default) =>
        _client.ChallengeAsync(kind, title, message, fields, cancellationToken);

    public ValueTask CompleteAuthorizationAsync(
        Guid interactionId,
        CancellationToken cancellationToken = default) =>
        _client.CompleteAuthorizationAsync(interactionId, cancellationToken);

    public ValueTask<string?> ReadSecretAsync(string key, CancellationToken cancellationToken = default) =>
        _client.ReadSecretAsync(key, cancellationToken);

    public ValueTask WriteSecretAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default) =>
        _client.WriteSecretAsync(key, value, cancellationToken);

    public ValueTask DeleteSecretAsync(string key, CancellationToken cancellationToken = default) =>
        _client.DeleteSecretAsync(key, cancellationToken);

    internal void SetStreams(IReadOnlyDictionary<string, CollectorClientStream> streams)
    {
        if (_streams is not null)
            throw new InvalidOperationException("Collector Fact Streams are already open.");
        _streams = streams;
    }

    internal CollectorClientStream Stream(string bindingId) =>
        Streams.TryGetValue(bindingId, out var stream)
            ? stream
            : throw new InvalidOperationException($"Collector Fact Stream '{bindingId}' is not open.");
}
