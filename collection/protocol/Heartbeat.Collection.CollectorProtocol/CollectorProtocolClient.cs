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
    private readonly CollectorDeliveryOwnership _deliveryOwnership = new(
        binding.TryCommitAcknowledgement);
    private CollectorProtocolOutbox? _outbox;
    private CollectorActivation? _activation;
    private Task? _backgroundFlush;
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
            _timeProvider.GetUtcNow(),
            binding.TryPublishDurableFile);
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
        var backgroundDelivery = _deliveryOwnership.BeginBackground();
        var backgroundFlush = FlushInBackgroundAsync(backgroundDelivery, applicationLifetime.Token);
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
        var drainTransition = _deliveryOwnership.BeginDrain(drain);
        using var drainCancellation = drainTransition.BeginCancellation(
            cancellationToken,
            deadlineTimer.Token);
        try
        {
            var drainContext = new CollectorDrainContext(this, drainTransition);
            var reason = CollectorProtocolDrainReason.Drained;
            var remainderDurable = true;
            try
            {
                await startInvocationReturned.Task.WaitAsync(drainCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                drainCancellation.FirstCause == CollectorDrainCancellationCause.Deadline)
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
                await cancelApplication.WaitAsync(drainCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                drainCancellation.FirstCause == CollectorDrainCancellationCause.Deadline)
            {
                reason = CollectorProtocolDrainReason.DeadlineExceeded;
                remainderDurable = false;
                Observe(cancelApplication);
            }
            try
            {
                await backgroundFlush.WaitAsync(drainCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                drainCancellation.FirstCause == CollectorDrainCancellationCause.Deadline)
            {
                Observe(backgroundFlush);
                if (reason == CollectorProtocolDrainReason.Drained)
                    reason = CollectorProtocolDrainReason.DeadlineExceeded;
            }
            catch (OperationCanceledException) when (
                drainCancellation.FirstCause == CollectorDrainCancellationCause.Caller)
            {
                throw;
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
            if (drainCancellation.FirstCause == CollectorDrainCancellationCause.None)
            {
                stopTask = Task.Run(
                    async () => await application.StopAsync(drainContext, drainCancellation.Token).ConfigureAwait(false),
                    CancellationToken.None);
                try
                {
                    await stopTask.WaitAsync(drainCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    drainCancellation.FirstCause == CollectorDrainCancellationCause.Deadline)
                {
                    reason = _persistenceFailed || Volatile.Read(ref _activePersistenceRetries) != 0
                        ? CollectorProtocolDrainReason.PersistenceFailed
                        : CollectorProtocolDrainReason.DeadlineExceeded;
                    remainderDurable = false;
                    Observe(stopTask);
                }
                catch (OperationCanceledException) when (
                    drainCancellation.FirstCause == CollectorDrainCancellationCause.Caller)
                {
                    throw;
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
            drainTransition.SealTailAdmission();
            Task? finalFlush = null;
            var finalFlushOutcome = CollectorDeliveryStepResult.Progressed;
            try
            {
                finalFlush = Task.Run(
                    async () => finalFlushOutcome = await FlushAsync(
                        drainTransition.Delivery,
                        drainCancellation.Token).ConfigureAwait(false),
                    CancellationToken.None);
                await finalFlush.WaitAsync(drainCancellation.Token).ConfigureAwait(false);
                if (finalFlushOutcome == CollectorDeliveryStepResult.Fenced &&
                    reason == CollectorProtocolDrainReason.Drained)
                    reason = CollectorProtocolDrainReason.FlushCancelled;
            }
            catch (OperationCanceledException) when (
                drainCancellation.FirstCause == CollectorDrainCancellationCause.Deadline)
            {
                if (finalFlush is not null)
                    Observe(finalFlush);
                if (reason == CollectorProtocolDrainReason.Drained)
                    reason = CollectorProtocolDrainReason.FlushCancelled;
            }
            catch (OperationCanceledException) when (
                drainCancellation.FirstCause == CollectorDrainCancellationCause.Caller)
            {
                throw;
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
            var result = new CollectorDrainResult(
                initialization.SpecRevision,
                _outbox.Facts.Count,
                _outbox.Gaps.Count,
                reason,
                remainderDurable);
            try
            {
                var completion = Task.Run(
                    async () => await binding.CompleteDrainAsync(result, drainCancellation.Token).ConfigureAwait(false),
                    CancellationToken.None);
                try
                {
                    await completion.WaitAsync(drainCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    drainCancellation.FirstCause == CollectorDrainCancellationCause.Deadline)
                {
                    Observe(completion);
                    throw;
                }
                return new CollectorDrainExecutionResult(
                    result,
                    CollectorProtocolDrainCompletionReason.Completed);
            }
            catch (OperationCanceledException) when (
                drainCancellation.FirstCause == CollectorDrainCancellationCause.Deadline)
            {
                return new CollectorDrainExecutionResult(
                    result,
                    CollectorProtocolDrainCompletionReason.DeadlineExceeded);
            }
            catch (OperationCanceledException) when (
                drainCancellation.FirstCause == CollectorDrainCancellationCause.Caller)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new CollectorDrainExecutionResult(
                    result,
                    CollectorProtocolDrainCompletionReason.CompletionFailed,
                    exception.Message);
            }
        }
        finally
        {
            drainTransition.Fence();
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
        var admission = _deliveryOwnership.BeginOrdinaryAdmission();
        await PublishBatchAsync(facts, admission, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask PublishDrainBatchAsync(
        CollectorDrainTransition transition,
        IReadOnlyList<CollectorFact> facts,
        CancellationToken cancellationToken)
    {
        var admission = transition.BeginTailAdmission();
        await PublishBatchAsync(facts, admission, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishBatchAsync(
        IReadOnlyList<CollectorFact> facts,
        CollectorAdmissionLease admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0)
            return;
        foreach (var fact in facts)
            EnsureBinding(fact.BindingId);
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await PersistAdmissionMutationAsync(
                () => _outbox!.EnqueueFacts(facts, admission),
                cancellationToken).ConfigureAwait(false);
            if (outcome != CollectorAdmissionOutcome.Committed)
                throw new CollectorAdmissionClosedException();
        }
        finally
        {
            _deliveryGate.Release();
        }
        SignalFlush();
    }

    internal async ValueTask ReportGapAsync(CollectorStreamGap gap, CancellationToken cancellationToken)
    {
        var admission = _deliveryOwnership.BeginOrdinaryAdmission();
        await ReportGapAsync(gap, admission, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ReportDrainGapAsync(
        CollectorDrainTransition transition,
        CollectorStreamGap gap,
        CancellationToken cancellationToken)
    {
        var admission = transition.BeginTailAdmission();
        await ReportGapAsync(gap, admission, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReportGapAsync(
        CollectorStreamGap gap,
        CollectorAdmissionLease admission,
        CancellationToken cancellationToken)
    {
        EnsureBinding(gap.BindingId);
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await PersistAdmissionMutationAsync(
                () => _outbox!.EnqueueGap(gap, admission),
                cancellationToken).ConfigureAwait(false);
            if (outcome != CollectorAdmissionOutcome.Committed)
                throw new CollectorAdmissionClosedException();
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

    private async Task<CollectorDeliveryStepResult> FlushAsync(
        CollectorDeliveryLease delivery,
        CancellationToken cancellationToken)
    {
        if (_activation is null || !_activation.HasStreams)
            return CollectorDeliveryStepResult.Progressed;
        while (_outbox!.HasPending)
        {
            var result = _outbox.FirstFact is not null
                ? await DeliverFactAsync(delivery, cancellationToken).ConfigureAwait(false)
                : _outbox.FirstGap is not null
                    ? await DeliverGapAsync(delivery, cancellationToken).ConfigureAwait(false)
                    : throw new InvalidDataException("Collector Protocol outbox delivery order is invalid.");
            if (result is CollectorDeliveryStepResult.Superseded or CollectorDeliveryStepResult.Fenced)
                return result;
        }
        return CollectorDeliveryStepResult.Progressed;
    }

    private async Task FlushInBackgroundAsync(
        CollectorDeliveryLease delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _flushSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                var result = await FlushAsync(delivery, cancellationToken).ConfigureAwait(false);
                if (result is CollectorDeliveryStepResult.Superseded or CollectorDeliveryStepResult.Fenced)
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<CollectorDeliveryStepResult> DeliverFactAsync(
        CollectorDeliveryLease delivery,
        CancellationToken cancellationToken)
    {
        PendingCollectorFact? pending;
        BoundCollectorFact fact;
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            pending = _outbox!.FirstFact;
            if (pending is null)
                return CollectorDeliveryStepResult.Progressed;
            fact = Bind(pending.Fact, _activation!.Stream(pending.Fact.BindingId));
        }
        finally
        {
            _deliveryGate.Release();
        }
        var acknowledgement = await binding.PublishAsync(
            pending.MessageId,
            [fact],
            cancellationToken).ConfigureAwait(false);
        var current = ToStepResult(delivery.Check());
        if (current != CollectorDeliveryStepResult.Progressed)
            return current;
        if (!acknowledgement.IsMessageRejected && acknowledgement.Results.Count != 1)
            throw new InvalidDataException("Collector Protocol returned an invalid Fact ACK count.");

        var retryAfter = default(int?);
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_outbox!.Facts.All(item => item.MessageId != pending.MessageId))
                return CollectorDeliveryStepResult.Progressed;
            CollectorDeliveryCommitOutcome commit;
            if (acknowledgement.IsMessageRejected)
            {
                commit = await PersistDeliveryMutationAsync(
                    () => _outbox.DeadLetter(
                        pending,
                        acknowledgement.MessageError!,
                        _timeProvider.GetUtcNow(),
                        delivery),
                    cancellationToken).ConfigureAwait(false);
                if (commit == CollectorDeliveryCommitOutcome.Committed)
                    ReportDiagnostics();
            }
            else
            {
                var outcome = acknowledgement.Results[0];
                if (outcome.Status == CollectorFactDeliveryStatus.Retry)
                {
                    commit = await PersistDeliveryMutationAsync(
                        () => _outbox.RetryFact(pending.MessageId, delivery),
                        cancellationToken).ConfigureAwait(false);
                    retryAfter = outcome.RetryAfterMilliseconds ?? 1_000;
                }
                else if (outcome.IsAcknowledged)
                {
                    commit = await PersistDeliveryMutationAsync(
                        () => _outbox.AcknowledgeFact(pending.MessageId, delivery),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    commit = await PersistDeliveryMutationAsync(
                        () => _outbox.DeadLetter(
                            pending,
                            outcome.Error ?? new CollectorProtocolError(
                                "fact_delivery_failed",
                                $"Hub returned '{outcome.Status}' for a Collector Fact.",
                                false),
                            _timeProvider.GetUtcNow(),
                            delivery),
                        cancellationToken).ConfigureAwait(false);
                    if (commit == CollectorDeliveryCommitOutcome.Committed)
                        ReportDiagnostics();
                }
            }
            current = ToStepResult(commit);
        }
        finally
        {
            _deliveryGate.Release();
        }
        if (current != CollectorDeliveryStepResult.Progressed)
            return current;
        if (retryAfter is { } milliseconds)
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
        return CollectorDeliveryStepResult.Progressed;
    }

    private async Task<CollectorDeliveryStepResult> DeliverGapAsync(
        CollectorDeliveryLease delivery,
        CancellationToken cancellationToken)
    {
        PendingCollectorGap? pending;
        Guid streamId;
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            pending = _outbox!.FirstGap;
            if (pending is null)
                return CollectorDeliveryStepResult.Progressed;
            streamId = _activation!.Stream(pending.Gap.BindingId).StreamId;
        }
        finally
        {
            _deliveryGate.Release();
        }
        var outcome = await binding.ReportGapAsync(
            pending.MessageId,
            streamId,
            pending.Gap,
            cancellationToken).ConfigureAwait(false);
        var current = ToStepResult(delivery.Check());
        if (current != CollectorDeliveryStepResult.Progressed)
            return current;
        var retryAfter = default(int?);
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_outbox!.Gaps.All(item => item.MessageId != pending.MessageId))
                return CollectorDeliveryStepResult.Progressed;
            CollectorDeliveryCommitOutcome commit;
            if (outcome.IsAcknowledged)
                commit = await PersistDeliveryMutationAsync(
                    () => _outbox.AcknowledgeGap(pending.MessageId, delivery),
                    cancellationToken).ConfigureAwait(false);
            else if (outcome.Status == CollectorGapDeliveryStatus.Retry || outcome.Error?.Retryable == true)
            {
                commit = await PersistDeliveryMutationAsync(
                    () => _outbox.RetryGap(pending.MessageId, delivery),
                    cancellationToken).ConfigureAwait(false);
                retryAfter = outcome.RetryAfterMilliseconds ?? 1_000;
            }
            else
                return CollectorDeliveryStepResult.Progressed;
            current = ToStepResult(commit);
        }
        finally
        {
            _deliveryGate.Release();
        }
        if (current != CollectorDeliveryStepResult.Progressed)
            return current;
        if (retryAfter is { } milliseconds)
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
        return CollectorDeliveryStepResult.Progressed;
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

    private async Task<CollectorAdmissionOutcome> PersistAdmissionMutationAsync(
        Func<CollectorAdmissionOutcome> mutation,
        CancellationToken cancellationToken) =>
        await PersistOwnedMutationAsync(
            mutation,
            "Collector Protocol could not persist the pending outbox mutation before drain deadline.",
            cancellationToken).ConfigureAwait(false);

    private async Task<CollectorDeliveryCommitOutcome> PersistDeliveryMutationAsync(
        Func<CollectorDeliveryCommitOutcome> mutation,
        CancellationToken cancellationToken) =>
        await PersistOwnedMutationAsync(
            mutation,
            "Collector Protocol could not persist the delivery outcome before drain deadline.",
            cancellationToken).ConfigureAwait(false);

    private async Task<T> PersistOwnedMutationAsync<T>(
        Func<T> mutation,
        string deadlineMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            return mutation();
        }
        catch (IOException)
        {
            // Owned mutations publish their proposed state only after the replacement commits,
            // so preparation and commit are safe to retry without another observation.
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
                        deadlineMessage,
                        exception);
                }
                try
                {
                    return mutation();
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

    private static CollectorDeliveryStepResult ToStepResult(
        CollectorDeliveryCommitOutcome outcome) => outcome switch
        {
            CollectorDeliveryCommitOutcome.Committed => CollectorDeliveryStepResult.Progressed,
            CollectorDeliveryCommitOutcome.Superseded => CollectorDeliveryStepResult.Superseded,
            CollectorDeliveryCommitOutcome.Fenced => CollectorDeliveryStepResult.Fenced,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

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

public sealed class CollectorDrainContext
{
    private readonly CollectorProtocolClient _client;
    private readonly CollectorDrainTransition _transition;

    internal CollectorDrainContext(
        CollectorProtocolClient client,
        CollectorDrainTransition transition)
    {
        _client = client;
        _transition = transition;
    }

    public DateTimeOffset Deadline => _transition.Deadline;

    public ValueTask PublishAsync(
        CollectorFact fact,
        CancellationToken cancellationToken = default) =>
        _client.PublishDrainBatchAsync(_transition, [fact], cancellationToken);

    public ValueTask PublishBatchAsync(
        IReadOnlyList<CollectorFact> facts,
        CancellationToken cancellationToken = default) =>
        _client.PublishDrainBatchAsync(_transition, facts, cancellationToken);

    public ValueTask ReportGapAsync(
        CollectorStreamGap gap,
        CancellationToken cancellationToken = default) =>
        _client.ReportDrainGapAsync(_transition, gap, cancellationToken);
}
