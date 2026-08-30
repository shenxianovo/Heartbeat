namespace Heartbeat.Collection.CollectorProtocol;

/// <summary>
/// Collector-side protocol module. Callers provide observations through
/// <see cref="CollectorActivation"/>; this module owns lifecycle, durable delivery, ACK/retry,
/// Gap, authorization, Collector Secret, and drain semantics.
/// </summary>
public sealed class CollectorProtocolClient(
    CollectorClientDefinition definition,
    ICollectorProtocolBinding binding,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private readonly SemaphoreSlim _flushSignal = new(0, 1);
    private readonly CollectorDeliveryCommitFence _deliveryCommitFence = new(
        commitBoundary: binding.TryCommitAcknowledgement);
    private CollectorProtocolOutbox? _outbox;
    private CollectorActivation? _activation;
    private Task? _backgroundFlush;
    private volatile bool _admissionOpen = true;
    private volatile bool _persistenceFailed;
    private int _activePersistenceRetries;

    public async Task<CollectorDrainExecutionResult> RunAsync(
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
            _timeProvider.GetUtcNow());
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
        using var applicationLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var drainTask = binding.WaitForDrainAsync(cancellationToken).AsTask();
        var backgroundFlush = FlushInBackgroundAsync(applicationLifetime.Token);
        _backgroundFlush = backgroundFlush;
        SignalFlush();
        var startInvocationReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationTask = Task.Run(async () =>
        {
            try
            {
                var start = application.StartAsync(_activation, applicationLifetime.Token);
                startInvocationReturned.TrySetResult();
                await start.ConfigureAwait(false);
            }
            catch
            {
                startInvocationReturned.TrySetResult();
                throw;
            }
        }, CancellationToken.None);
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
            Observe(applicationTask);
        }
        using var deadlineTimer = CreateDeadlineCancellation(drain.Deadline);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineTimer.Token);
        _deliveryCommitFence.Fence();
        using var deadlineFence = deadline.Token.Register(
            FenceAdmission);
        var reason = CollectorProtocolDrainReason.Drained;
        var remainderDurable = true;
        try
        {
            await startInvocationReturned.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            reason = CollectorProtocolDrainReason.DeadlineExceeded;
            remainderDurable = false;
            Observe(applicationTask);
        }
        var cancelApplication = Task.Run(
            async () => await applicationLifetime.CancelAsync().ConfigureAwait(false),
            CancellationToken.None);
        try
        {
            await cancelApplication.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            reason = CollectorProtocolDrainReason.DeadlineExceeded;
            remainderDurable = false;
            Observe(cancelApplication);
        }
        try
        {
            await backgroundFlush.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Observe(backgroundFlush);
            if (reason == CollectorProtocolDrainReason.Drained)
                reason = CollectorProtocolDrainReason.DeadlineExceeded;
        }
        catch (IOException)
        {
            reason = CollectorProtocolDrainReason.PersistenceFailed;
            remainderDurable = false;
        }
        catch
        {
            reason = CollectorProtocolDrainReason.FlushCancelled;
        }

        Task? stopTask = null;
        if (!deadline.IsCancellationRequested)
        {
            stopTask = Task.Run(
                async () => await application.StopAsync(_activation, deadline.Token).ConfigureAwait(false),
                CancellationToken.None);
            try
            {
                await stopTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                reason = _persistenceFailed || Volatile.Read(ref _activePersistenceRetries) != 0
                    ? CollectorProtocolDrainReason.PersistenceFailed
                    : CollectorProtocolDrainReason.DeadlineExceeded;
                remainderDurable = false;
                Observe(stopTask);
            }
            catch (IOException)
            {
                reason = CollectorProtocolDrainReason.PersistenceFailed;
                remainderDurable = false;
            }
            catch
            {
                reason = CollectorProtocolDrainReason.StopFailed;
                remainderDurable = false;
            }
        }

        _admissionOpen = false;
        Task? finalFlush = null;
        try
        {
            finalFlush = Task.Run(
                () => FlushAsync(deadline.Token),
                CancellationToken.None);
            await finalFlush.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (finalFlush is not null)
                Observe(finalFlush);
            if (reason == CollectorProtocolDrainReason.Drained)
                reason = CollectorProtocolDrainReason.FlushCancelled;
        }
        catch (IOException)
        {
            reason = CollectorProtocolDrainReason.PersistenceFailed;
            remainderDurable = false;
        }
        catch
        {
            if (reason == CollectorProtocolDrainReason.Drained)
                reason = CollectorProtocolDrainReason.FlushCancelled;
        }
        if (deadline.IsCancellationRequested)
            FenceAdmission();
        var result = new CollectorDrainResult(
            initialization.SpecRevision,
            _outbox.Facts.Count,
            _outbox.Gaps.Count,
            reason,
            remainderDurable);
        try
        {
            var completion = Task.Run(
                async () => await binding.CompleteDrainAsync(result, deadline.Token).ConfigureAwait(false),
                CancellationToken.None);
            try
            {
                await completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Observe(completion);
                throw;
            }
            return new CollectorDrainExecutionResult(
                result,
                CollectorProtocolDrainCompletionReason.Completed);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new CollectorDrainExecutionResult(
                result,
                CollectorProtocolDrainCompletionReason.DeadlineExceeded);
        }
        catch (Exception exception)
        {
            return new CollectorDrainExecutionResult(
                result,
                CollectorProtocolDrainCompletionReason.CompletionFailed,
                exception.Message);
        }
    }

    internal IReadOnlyList<CollectorFact> PendingFacts =>
        _outbox?.Facts.Select(item => item.Fact).ToArray() ?? [];

    internal async ValueTask PublishAsync(CollectorFact fact, CancellationToken cancellationToken)
        => await PublishBatchAsync([fact], cancellationToken).ConfigureAwait(false);

    internal async ValueTask PublishBatchAsync(
        IReadOnlyList<CollectorFact> facts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0)
            return;
        foreach (var fact in facts)
            EnsureBinding(fact.BindingId);
        EnsureAdmissionOpen();
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAdmissionOpen();
            await PersistMutationAsync(() => _outbox!.EnqueueFacts(facts), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deliveryGate.Release();
        }
        SignalFlush();
    }

    internal async ValueTask ReportGapAsync(CollectorStreamGap gap, CancellationToken cancellationToken)
    {
        EnsureBinding(gap.BindingId);
        EnsureAdmissionOpen();
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAdmissionOpen();
            await PersistMutationAsync(() => _outbox!.EnqueueGap(gap), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deliveryGate.Release();
        }
        SignalFlush();
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
        if (_activation is null || !_activation.HasStreams)
            return;
        await FlushFactsAsync(cancellationToken).ConfigureAwait(false);
        await FlushGapsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _flushSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Drain owns the final bounded flush after stopping application ingress.
        }
    }

    private async Task FlushFactsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            PendingCollectorFact? pending;
            BoundCollectorFact fact;
            await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                pending = _outbox!.Facts.FirstOrDefault();
                if (pending is null)
                    return;
                fact = Bind(pending.Fact, _activation!.Stream(pending.Fact.BindingId));
            }
            finally
            {
                _deliveryGate.Release();
            }
            var deliveryEpoch = _deliveryCommitFence.CaptureEpoch();
            var acknowledgement = await binding.PublishAsync(
                pending.MessageId,
                [fact],
                cancellationToken).ConfigureAwait(false);
            ThrowIfDeliverySuperseded(deliveryEpoch, cancellationToken);
            if (!acknowledgement.IsMessageRejected && acknowledgement.Results.Count != 1)
                throw new InvalidDataException("Collector Protocol returned an invalid Fact ACK count.");

            var retryAfter = default(int?);
            await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDeliverySuperseded(deliveryEpoch, cancellationToken);
                if (_outbox!.Facts.All(item => item.MessageId != pending.MessageId))
                    continue;
                if (acknowledgement.IsMessageRejected)
                {
                    await PersistDeliveryMutationAsync(
                        () => _outbox.DeadLetter(
                            pending,
                            acknowledgement.MessageError!,
                            _timeProvider.GetUtcNow(),
                            _deliveryCommitFence,
                            deliveryEpoch),
                        cancellationToken).ConfigureAwait(false);
                    ReportDiagnostics();
                    continue;
                }
                var outcome = acknowledgement.Results[0];
                if (outcome.Status == CollectorFactDeliveryStatus.Retry)
                {
                    await PersistDeliveryMutationAsync(
                        () => _outbox.RetryFact(pending.MessageId, _deliveryCommitFence, deliveryEpoch),
                        cancellationToken).ConfigureAwait(false);
                    retryAfter = outcome.RetryAfterMilliseconds ?? 1_000;
                }
                else if (outcome.IsAcknowledged)
                {
                    await PersistDeliveryMutationAsync(
                        () => _outbox.AcknowledgeFact(pending.MessageId, _deliveryCommitFence, deliveryEpoch),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await PersistDeliveryMutationAsync(
                        () => _outbox.DeadLetter(
                            pending,
                            outcome.Error ?? new CollectorProtocolError(
                                "fact_delivery_failed",
                                $"Hub returned '{outcome.Status}' for a Collector Fact.",
                                false),
                            _timeProvider.GetUtcNow(),
                            _deliveryCommitFence,
                            deliveryEpoch),
                        cancellationToken).ConfigureAwait(false);
                    ReportDiagnostics();
                }
            }
            finally
            {
                _deliveryGate.Release();
            }
            if (retryAfter is { } milliseconds)
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlushGapsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            PendingCollectorGap? pending;
            Guid streamId;
            await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                pending = _outbox!.Gaps.FirstOrDefault();
                if (pending is null)
                    return;
                streamId = _activation!.Stream(pending.Gap.BindingId).StreamId;
            }
            finally
            {
                _deliveryGate.Release();
            }
            var deliveryEpoch = _deliveryCommitFence.CaptureEpoch();
            var outcome = await binding.ReportGapAsync(
                pending.MessageId,
                streamId,
                pending.Gap,
                cancellationToken).ConfigureAwait(false);
            ThrowIfDeliverySuperseded(deliveryEpoch, cancellationToken);
            var retryAfter = default(int?);
            await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDeliverySuperseded(deliveryEpoch, cancellationToken);
                if (_outbox!.Gaps.All(item => item.MessageId != pending.MessageId))
                    continue;
                if (outcome.IsAcknowledged)
                {
                    await PersistDeliveryMutationAsync(
                        () => _outbox.AcknowledgeGap(pending.MessageId, _deliveryCommitFence, deliveryEpoch),
                        cancellationToken).ConfigureAwait(false);
                }
                else if (outcome.Status == CollectorGapDeliveryStatus.Retry || outcome.Error?.Retryable == true)
                {
                    await PersistDeliveryMutationAsync(
                        () => _outbox.RetryGap(pending.MessageId, _deliveryCommitFence, deliveryEpoch),
                        cancellationToken).ConfigureAwait(false);
                    retryAfter = outcome.RetryAfterMilliseconds ?? 1_000;
                }
                else
                {
                    return;
                }
            }
            finally
            {
                _deliveryGate.Release();
            }
            if (retryAfter is { } milliseconds)
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
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

        Interlocked.Increment(ref _activePersistenceRetries);
        var delay = TimeSpan.FromMilliseconds(50);
        try
        {
            while (true)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    _persistenceFailed = true;
                    throw new CollectorProtocolPersistenceException(
                        "Collector Protocol could not persist the pending outbox mutation before drain deadline.",
                        exception);
                }
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
        finally
        {
            Interlocked.Decrement(ref _activePersistenceRetries);
        }
    }

    private async Task PersistDeliveryMutationAsync(Action mutation, CancellationToken cancellationToken)
    {
        try
        {
            mutation();
            return;
        }
        catch (IOException)
        {
            // Delivery mutations publish their proposed state only after the replacement commits,
            // so the whole mutation is safe to retry.
        }

        Interlocked.Increment(ref _activePersistenceRetries);
        var delay = TimeSpan.FromMilliseconds(50);
        try
        {
            while (true)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    _persistenceFailed = true;
                    throw new CollectorProtocolPersistenceException(
                        "Collector Protocol could not persist the delivery outcome before drain deadline.",
                        exception);
                }
                try
                {
                    mutation();
                    return;
                }
                catch (IOException)
                {
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1_000));
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activePersistenceRetries);
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

    private void EnsureAdmissionOpen()
    {
        if (!_admissionOpen)
            throw new InvalidOperationException("Collector Protocol admission is fenced after drain.");
    }

    private void SignalFlush()
    {
        try
        {
            _flushSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another publisher already queued the single coalesced flush signal.
        }
    }

    private void FenceAdmission()
    {
        _admissionOpen = false;
        _deliveryCommitFence.Fence();
    }

    private void ThrowIfDeliverySuperseded(int deliveryEpoch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deliveryEpoch != _deliveryCommitFence.CaptureEpoch())
            throw new OperationCanceledException("Collector delivery attempt was superseded by drain.");
        binding.ThrowIfAcknowledgementSuperseded();
    }

    private CancellationTokenSource CreateDeadlineCancellation(DateTimeOffset deadline)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero
            ? new CancellationTokenSource(remaining, _timeProvider)
            : new CancellationTokenSource(TimeSpan.Zero, _timeProvider);
    }

    private static void Observe(Task task) => _ = task.ContinueWith(
        static completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
        TaskScheduler.Default);

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
        await binding.DisposeAsync().ConfigureAwait(false);
        if (_backgroundFlush is not { IsCompleted: false })
        {
            _deliveryGate.Dispose();
            _flushSignal.Dispose();
        }
    }
}

internal sealed class CollectorProtocolPersistenceException(string message, Exception innerException)
    : IOException(message, innerException);

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

    public ValueTask PublishBatchAsync(
        IReadOnlyList<CollectorFact> facts,
        CancellationToken cancellationToken = default) =>
        _client.PublishBatchAsync(facts, cancellationToken);

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
