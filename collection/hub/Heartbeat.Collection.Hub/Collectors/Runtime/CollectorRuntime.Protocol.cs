using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Input;
using Serilog;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public sealed partial class CollectorRuntime
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<int>> HubProtocolCapabilities =
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["facts.segment"] = [1],
            ["facts.event"] = [1],
            ["auth.interactive"] = [1],
            ["secrets.instance"] = [1],
            ["resources.instance-data"] = [1],
            ["diagnostics.stream-gap"] = [1]
        };

    private readonly Dictionary<Guid, InProcessCollectorActivation> _activations = [];
    private readonly Dictionary<Guid, PendingActivationCommit> _pendingActivationCommits = [];
    // Legacy activity/input projections read only the fields their consumers understand.
    private readonly ActivitySegmentFactProjector _segmentProjector;
    private readonly InputEventFactProjector _inputEventProjector = new();
    private readonly Dictionary<Guid, Guid> _streamWriters = [];
    private readonly HashSet<Guid> _startingInstances = [];
    private readonly Dictionary<Guid, CollectorActivationLifetime> _activationLifetimes = [];
    private readonly object _helloAttemptGate = new();
    private readonly Dictionary<(Guid InstanceId, Guid MessageId), HelloAttempt> _helloAttempts = [];

    public ValueTask<InProcessCollectorActivation> ActivateInProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        IInProcessCollector collector,
        CancellationToken cancellationToken = default) =>
        ActivateInProcessAsync(
            collectorInstanceId,
            package,
            collector,
            Guid.CreateVersion7(),
            cancellationToken);

    public ValueTask<InProcessCollectorActivation> ActivateInProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        IInProcessCollector collector,
        Guid helloMessageId,
        CancellationToken cancellationToken = default) =>
        ActivateProtocolAsync(
            collectorInstanceId,
            package,
            collector,
            "inProcess",
            helloMessageId,
            cancellationToken);

    private async ValueTask<InProcessCollectorActivation> ActivateProtocolAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        IInProcessCollector collector,
        string executionDriver,
        Guid helloMessageId,
        CancellationToken cancellationToken,
        Func<CollectorActivationSession, ICollectorActivationLifetimeDriver>? lifetimeDriver = null,
        TimeSpan? drainBudget = null,
        Action<CollectorActivationLifetime>? lifetimeCreated = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(collector);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUuidV7(helloMessageId))
            throw ActivationError(
                "protocol_invalid_message",
                "activation.hello messageId must be a UUIDv7.");

        string artifactId;
        ProtocolSupport? protocolSupport;
        try
        {
            artifactId = collector.ArtifactId;
            protocolSupport = SnapshotProtocolSupport(collector.ProtocolSupport);
        }
        catch (Exception exception)
        {
            throw ActivationError(
                "protocol_invalid_message",
                "InProcess Collector failed to provide activation.hello fields.",
                exception);
        }

        string helloRequestHash;
        try
        {
            helloRequestHash = HelloRequestHash(
                collectorInstanceId,
                package,
                artifactId,
                protocolSupport);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException)
        {
            throw ActivationError(
                "protocol_invalid_message",
                "InProcess Collector returned malformed activation.hello fields.",
                exception);
        }
        HelloAttempt? helloAttempt = null;
        Task<InProcessCollectorActivation>? replayTask = null;
        lock (_helloAttemptGate)
        {
            if (_helloAttempts.TryGetValue((collectorInstanceId, helloMessageId), out var existing))
            {
                if (existing.RequestHash != helloRequestHash)
                    throw ActivationError(
                        "protocol_invalid_message",
                        "The same activation.hello messageId was reused with different content.");
                replayTask = existing.Completion.Task;
            }
            else
            {
                helloAttempt = new HelloAttempt(
                    helloRequestHash,
                    new TaskCompletionSource<InProcessCollectorActivation>(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                _helloAttempts.Add((collectorInstanceId, helloMessageId), helloAttempt);
            }
        }
        if (replayTask is not null)
            return await replayTask.WaitAsync(cancellationToken);
        var ownedHelloAttempt = helloAttempt!;
        var registeredStartingInstance = false;
        InProcessCollectorActivation? activation = null;
        CollectorActivationSession? session = null;
        CollectorActivationLifetime? lifetime = null;

        try
        {
            CollectorInstance instance;
            VerifiedCollectorArtifact artifact;
            Guid activationId;
            lock (_gate)
            {
                ThrowIfDisposed();
                var instanceState = GetInstanceStateLocked(collectorInstanceId);
                ValidatePackageCandidate(instanceState, package);
                instance = ToPublic(instanceState) with
                {
                    PackageVersion = package.Manifest.Version,
                    PackageContentHash = package.PackageContentHash
                };
                artifact = ResolveProtocolArtifact(
                    package,
                    artifactId,
                    executionDriver);
                ValidateProtocolSupport(package, protocolSupport);
                activationId = NextUniqueId(
                    id => _activations.ContainsKey(id) ||
                          _state.ActivationAttemptTombstones.Any(attempt => attempt.ActivationId == id),
                    "Collector Activation");
                PersistActivationAttemptTombstoneLocked(
                    collectorInstanceId,
                    helloMessageId,
                    helloRequestHash,
                    activationId);
                if (_startingInstances.Contains(collectorInstanceId) ||
                    HasExternalHostActivationLocked(collectorInstanceId) ||
                    _activations.Values.Any(activation =>
                        activation.State != CollectorActivationState.Stopped &&
                        activation.Streams.Values.Any(stream =>
                            stream.Descriptor.CollectorInstanceId == collectorInstanceId)))
                    throw ActivationError(
                        "stream_writer_conflict",
                        "Stop the current Collector Activation before starting its replacement.");
                _startingInstances.Add(collectorInstanceId);
                registeredStartingInstance = true;
                session = CreateActivationSession(
                    activationId,
                    helloMessageId,
                    package,
                    ActivationDeliveryCapability.Complete);
                lifetime = new CollectorActivationLifetime(
                    lifetimeDriver?.Invoke(session) ??
                        new InProcessCollectorActivationLifetimeDriver(collector, session),
                    session.FenceDeliveryAfterDeadline,
                    () => CompleteActivationLifetime(
                        collectorInstanceId,
                        activationId,
                        helloMessageId,
                        activation),
                    _options.TimeProvider,
                    drainBudget ?? _options.InProcessDrainGracePeriod);
                _preparationClosed = true;
                _activationLifetimes.Add(activationId, lifetime);
                lifetimeCreated?.Invoke(lifetime);
            }

            using var activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime!.StopRequested);
            var activationToken = activationCancellation.Token;
            var initialization = new CollectorInitialization(
                activationId,
                instance,
                instance.Spec,
                artifact,
                new CollectorProtocolLimits(
                    _options.MaxFactsPerBatch,
                    _options.MaxBatchBytes),
                new CollectorResources(Path.Combine(
                    _instanceDataRoot,
                    collectorInstanceId.ToString("N")),
                    session!.DurableCommitFence));
            InProcessCollectorInitialization initialized;
            try
            {
                if (lifetime.HasStopIntent)
                    throw new OperationCanceledException(lifetime.StopRequested);
                initialized = await collector.InitializeAsync(initialization, activationToken);
                if (initialized is null)
                    throw ActivationError(
                        "protocol_invalid_message",
                        "InProcess Collector returned a null activation.initialized response.");
                initialized = SnapshotInitialization(initialized);
                session!.AcceptInitialized(initialized.AppliedSpecRevision, instance.Spec.SpecRevision);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw ActivationError(
                    "protocol_invalid_message",
                    "InProcess Collector rejected or failed activation.initialize.",
                    exception);
            }

            activationToken.ThrowIfCancellationRequested();
            InProcessCollectorStreamsOpened opened;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (lifetime.HasStopIntent)
                    throw ActivationError(
                        "activation_stopping",
                        "Collector Activation is stopping before streams.open.");
                if (initialized.AppliedSpecRevision != instance.Spec.SpecRevision)
                    throw ActivationError(
                        "spec_revision_stale",
                        "Collector did not apply the current SpecRevision.");

                var streamPlan = PlanStreams(
                    activationId,
                    instance,
                    package,
                    initialized.Bindings);
                var descriptors = streamPlan.Bindings.ToImmutableDictionary(
                    pair => pair.BindingId,
                    pair => ToDescriptor(pair.Stream),
                    StringComparer.Ordinal);
                session!.AcceptStreams(descriptors);
                var streams = streamPlan.Bindings.ToImmutableDictionary(
                    pair => pair.BindingId,
                    pair => new InProcessFactStream(
                        session,
                        ToDescriptor(pair.Stream)),
                    StringComparer.Ordinal);
                activation = new InProcessCollectorActivation(
                    session,
                    lifetime,
                    streams);
                _activations.Add(activationId, activation);
                _pendingActivationCommits.Add(activationId, streamPlan.Commit);
                opened = new InProcessCollectorStreamsOpened(
                    activationId,
                    streams.ToImmutableDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Descriptor,
                        StringComparer.Ordinal),
                    () => CompleteCollectorReadyAsync(activation, lifetime));
            }

            try
            {
                await Task.Factory.StartNew(
                    async () =>
                    {
                        activationToken.ThrowIfCancellationRequested();
                        await collector.OnStreamsOpenedAsync(opened, activationToken);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CollectorActivationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw ActivationError(
                    "protocol_invalid_message",
                    "InProcess Collector failed to complete streams.open/ready.",
                    exception);
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                if (activation.State != CollectorActivationState.Ready)
                    throw ActivationError(
                        "protocol_invalid_message",
                        "InProcess Collector returned before sending activation.ready.");
                _startingInstances.Remove(collectorInstanceId);
                registeredStartingInstance = false;
            }
            ownedHelloAttempt.Completion.SetResult(activation);
            return activation;
        }
        catch (Exception exception)
        {
            if (lifetime is not null)
            {
                var terminal = await lifetime.RequestStopAsync(
                    new CollectorActivationStopIntent(CollectorActivationStopCause.ActivationFailed));
                if (!terminal.OwnershipReleased)
                {
                    Log.Warning(
                        terminal.ReleaseError,
                        "停止失败的 Collector Activation 后仍保留 ownership");
                }
            }
            else if (registeredStartingInstance)
            {
                lock (_gate)
                    _startingInstances.Remove(collectorInstanceId);
            }
            ownedHelloAttempt.Completion.TrySetException(exception);
            _ = ownedHelloAttempt.Completion.Task.Exception;
            throw;
        }
    }

    private FactBatchAcknowledgement CommitFacts(
        Guid activationId,
        Guid streamId,
        IReadOnlyList<FactSubmission> facts,
        ActivationDeliveryFence deliveryFence)
    {
        var results = new List<FactDeliveryOutcome>(facts.Count);
        for (var index = 0; index < facts.Count; index++)
            results.Add(CommitFact(activationId, index, facts[index], deliveryFence));
        MarkAcknowledgedLiveTraffic(streamId, results);
        return new FactBatchAcknowledgement(results);
    }

    private void CompleteActivationLifetime(
        Guid collectorInstanceId,
        Guid activationId,
        Guid helloMessageId,
        InProcessCollectorActivation? activation)
    {
        lock (_gate)
        {
            if (activation is not null)
            {
                foreach (var streamId in activation.Streams.Values.Select(stream => stream.Descriptor.StreamId))
                {
                    if (_streamWriters.TryGetValue(streamId, out var writer) && writer == activationId)
                        _streamWriters.Remove(streamId);
                }
            }
            _activations.Remove(activationId);
            _activationLifetimes.Remove(activationId);
            _pendingActivationCommits.Remove(activationId);
            _startingInstances.Remove(collectorInstanceId);
        }
        if (activation is not null)
        {
            lock (_helloAttemptGate)
                _helloAttempts.Remove((collectorInstanceId, helloMessageId));
        }
    }

    private async ValueTask<InProcessCollectorActivation> CompleteCollectorReadyAsync(
        InProcessCollectorActivation activation,
        CollectorActivationLifetime lifetime)
    {
        long expectedSpecRevision;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingActivationCommits.TryGetValue(activation.ActivationId, out var pendingCommit))
                throw ActivationError(
                    "protocol_invalid_message",
                    "Collector Activation has no pending Package and Stream state to commit.");
            expectedSpecRevision = pendingCommit.Instance.SpecRevision;
        }
        var ready = await lifetime.PublishReadyAsync(new CollectorReadyPublication(() =>
            ValueTask.FromResult(PrepareCollectorReady(
                activation,
                expectedSpecRevision))));
        if (ready == CollectorReadyOutcome.Stopping)
            throw ActivationError(
                "activation_stopping",
                "Collector Activation is stopping before activation.ready.");
        return activation;
    }

    private CollectorReadyPreparedCommit PrepareCollectorReady(
        InProcessCollectorActivation activation,
        long expectedSpecRevision)
    {
        CollectorRuntimeState baseState;
        CollectorRuntimeState next;
        PendingActivationCommit pendingCommit;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingActivationCommits.TryGetValue(activation.ActivationId, out var currentPendingCommit))
                throw ActivationError(
                    "protocol_invalid_message",
                    "Collector Activation has no pending Package and Stream state to commit.");
            pendingCommit = currentPendingCommit;
            baseState = _state;
            next = baseState.WithInstanceAndStreams(
                pendingCommit.Instance,
                pendingCommit.Streams);
        }

        JsonCollectorRuntimeStore.PreparedReplacement replacement;
        try
        {
            replacement = _store.Prepare(next);
        }
        catch (CollectorRuntimeStateException exception)
        {
            throw ActivationError(
                "hub_backpressure",
                "Hub could not prepare the resolved Package and Fact Streams.",
                exception,
                retryable: true);
        }

        return new CollectorReadyPreparedCommit(
            () => TryCommitCollectorReady(
                activation,
                expectedSpecRevision,
                baseState,
                next,
                pendingCommit,
                replacement),
            replacement);
    }

    private bool TryCommitCollectorReady(
        InProcessCollectorActivation activation,
        long expectedSpecRevision,
        CollectorRuntimeState baseState,
        CollectorRuntimeState next,
        PendingActivationCommit pendingCommit,
        JsonCollectorRuntimeStore.PreparedReplacement replacement)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(_state, baseState) ||
                !_pendingActivationCommits.TryGetValue(activation.ActivationId, out var current) ||
                !ReferenceEquals(current, pendingCommit))
                return false;
            foreach (var stream in activation.Streams.Values)
            {
                if (_streamWriters.TryGetValue(stream.Descriptor.StreamId, out var writer) &&
                    writer != activation.ActivationId)
                    throw ActivationError(
                        "stream_writer_conflict",
                        "A previous Activation still holds the Fact Stream writer lease.");
            }
            activation.Session.AcceptReady(
                expectedSpecRevision,
                expectedSpecRevision,
                () =>
                {
                    try
                    {
                        replacement.Commit();
                    }
                    catch (CollectorRuntimeStateException exception)
                    {
                        throw ActivationError(
                            "hub_backpressure",
                            "Hub could not publish the resolved Package and Fact Streams.",
                            exception,
                            retryable: true);
                    }
                    _state = next;
                    _pendingActivationCommits.Remove(activation.ActivationId);

                    foreach (var stream in activation.Streams.Values)
                        _streamWriters[stream.Descriptor.StreamId] = activation.ActivationId;
                });
            return true;
        }
    }

    private GapDeliveryOutcome CommitGap(
        Guid activationId,
        Guid streamId,
        StreamGapReport gap,
        ActivationDeliveryFence deliveryFence)
    {
        while (true)
        {
            CollectorRuntimeState baseState;
            CollectorRuntimeState next;
            GapDeliveryOutcome outcome;
            string fencedMessage;
            lock (_gate)
            {
                ThrowIfDeliveryUnavailable(activationId);
                if (!_streamWriters.TryGetValue(streamId, out var activeWriter) || activeWriter != activationId)
                    return GapRejected(
                        streamId,
                        "stream_writer_conflict",
                        "Activation does not hold this Fact Stream writer lease.");

                if (_state.Gaps.FirstOrDefault(existing =>
                        existing.StreamId == streamId &&
                        (existing.GapId == gap.GapId || existing.LegacyGapIdAlias == gap.GapId)) is { } existing)
                {
                    return existing.Start == gap.Start && existing.End == gap.End &&
                           existing.Reason == gap.Reason &&
                           existing.EstimatedFactsLost == gap.EstimatedFactsLost
                        ? new GapDeliveryOutcome(streamId, GapDeliveryStatus.Duplicate)
                        : GapRejected(
                            streamId,
                            "gap_identity_conflict",
                            "GapId was already committed with different content.");
                }

                baseState = _state;
                // Runtime state schema v2 wrote committed Gaps without GapId. Bind an exact lost-ACK
                // replay once; removal follows the state inventory/window in compatibility-debt.md.
                if (_state.Gaps.FirstOrDefault(existing =>
                        (existing.GapId == Guid.Empty || existing.AwaitingLegacyGapIdentity) &&
                        existing.StreamId == streamId && existing.Start == gap.Start &&
                        existing.End == gap.End && existing.Reason == gap.Reason &&
                        existing.EstimatedFactsLost == gap.EstimatedFactsLost) is { } currentFormatGap)
                {
                    next = currentFormatGap.GapId == Guid.Empty
                        ? baseState.WithBoundGapIdentity(currentFormatGap, gap.GapId)
                        : baseState.WithLegacyGapAlias(currentFormatGap, gap.GapId);
                    outcome = new GapDeliveryOutcome(streamId, GapDeliveryStatus.Duplicate);
                    fencedMessage = "Hub drain deadline fenced the Stream Gap identity commit.";
                }
                else
                {
                    if (_state.Gaps.Count(item => !_options.EnableFactUpload || !item.Delivered) >= _options.MaxDurableFacts)
                        return GapRetry(streamId, "Hub durable Gap inbox is applying backpressure.");
                    // Keep the finite migrated alias cohort: forgetting an original GapId would
                    // turn a later lost-ACK replay into a second Analytics loss report. Only
                    // ordinary delivered Gaps participate in the bounded replay window.
                    next = baseState.WithGap(new CommittedGapState
                        {
                            StreamId = streamId,
                            GapId = gap.GapId,
                            Start = gap.Start,
                            End = gap.End,
                            Reason = gap.Reason,
                            EstimatedFactsLost = gap.EstimatedFactsLost
                        }, _options.EnableFactUpload && _state.Gaps.Count(existing =>
                                !existing.AwaitingLegacyGapIdentity && existing.LegacyGapIdAlias is null) >= _options.MaxDurableFacts
                            ? _state.Gaps.FirstOrDefault(existing => existing.Delivered &&
                                !existing.AwaitingLegacyGapIdentity && existing.LegacyGapIdAlias is null) : null);
                    outcome = new GapDeliveryOutcome(streamId, GapDeliveryStatus.Committed);
                    fencedMessage = "Hub drain deadline fenced the Stream Gap commit.";
                }
            }

            JsonCollectorRuntimeStore.PreparedReplacement replacement;
            try
            {
                replacement = _store.Prepare(next);
            }
            catch (CollectorRuntimeStateException)
            {
                return GapRetry(streamId, "Hub could not persist the Stream Gap and is applying backpressure.");
            }
            using (replacement)
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_state, baseState))
                        continue;
                    if (deliveryFence.IsFenced)
                        return GapRetry(streamId, fencedMessage);
                    if (!_streamWriters.TryGetValue(streamId, out var activeWriter) ||
                        activeWriter != activationId)
                        return GapRejected(
                            streamId,
                            "stream_writer_conflict",
                            "Activation does not hold this Fact Stream writer lease.");
                    try
                    {
                        if (!deliveryFence.TryCommitHost(() =>
                            {
                                replacement.Commit();
                                _state = next;
                            }))
                            return GapRetry(streamId, fencedMessage);
                    }
                    catch (CollectorRuntimeStateException)
                    {
                        return GapRetry(streamId, "Hub could not persist the Stream Gap and is applying backpressure.");
                    }
                }
            }
            return outcome;
        }
    }

    private FactDeliveryOutcome CommitFact(
        Guid activationId,
        int index,
        FactSubmission fact,
        ActivationDeliveryFence deliveryFence)
    {
        PreparedFactCommit prepared;
        lock (_gate)
        {
            ThrowIfDeliveryUnavailable(activationId);
            if (PrepareFactLocked(activationId, index, fact, out prepared) is { } immediate)
                return immediate;
        }

        if (!_options.EnableFactUpload && prepared.Stream.FactKind == FactKind.Event &&
            !ProjectEvent(prepared.Stream, prepared.Committed, isReplay: false, deliveryFence))
            return Retry(index, "Hub durable Event projection is applying backpressure.");

        while (true)
        {
            CollectorRuntimeState baseState;
            CollectorRuntimeState next;
            lock (_gate)
            {
                if (PrepareFactLocked(activationId, index, fact, out prepared) is { } immediate)
                    return immediate;
                baseState = _state;
                next = baseState.WithFact(prepared.Committed, prepared.EvictedEvent);
            }

            JsonCollectorRuntimeStore.PreparedReplacement replacement;
            try
            {
                replacement = _store.Prepare(next);
            }
            catch (CollectorRuntimeStateException)
            {
                return Retry(index, "Hub could not persist the Fact and is applying backpressure.");
            }
            using (replacement)
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_state, baseState))
                        continue;
                    if (!_streamWriters.TryGetValue(fact.StreamId, out var writer) || writer != activationId)
                        return Rejected(
                            index,
                            "stream_writer_conflict",
                            "Activation does not hold this Fact Stream writer lease.");
                    try
                    {
                        if (!deliveryFence.TryCommitHost(() =>
                            {
                                replacement.Commit();
                                _state = next;
                            }))
                            return Retry(index, "Hub drain deadline fenced the durable Fact commit.");
                    }
                    catch (CollectorRuntimeStateException)
                    {
                        return Retry(index, "Hub could not persist the Fact and is applying backpressure.");
                    }
                }
            }
            break;
        }

        if (_options.EnableFactUpload)
            ObserveCommittedFact(prepared.Stream, prepared.Committed);
        else if (prepared.Stream.FactKind != FactKind.Event)
            ProjectFact(prepared.Stream, prepared.Committed, isReplay: false);
        return new FactDeliveryOutcome(index, FactDeliveryStatus.Committed);
    }

    private FactDeliveryOutcome? PrepareFactLocked(
        Guid activationId,
        int index,
        FactSubmission fact,
        out PreparedFactCommit prepared)
    {
        prepared = default!;
        if (!_streamWriters.TryGetValue(fact.StreamId, out var writer) || writer != activationId)
            return Rejected(index, "stream_writer_conflict", "Activation does not hold this Fact Stream writer lease.");
        var activationState = _activations.TryGetValue(activationId, out var inProcessActivation)
            ? inProcessActivation.State
            : _externalHostActivations.TryGetValue(activationId, out var externalHostActivation)
                ? externalHostActivation.State
                : CollectorActivationState.Stopped;
        if (activationState is not (CollectorActivationState.Ready or CollectorActivationState.Draining))
            return Rejected(index, "activation_stopping", "Collector Activation cannot deliver Facts in its current state.");

        var stream = _state.Streams.SingleOrDefault(candidate => candidate.StreamId == fact.StreamId);
        if (stream is null)
            return Rejected(index, "fact_invalid", "Fact Stream does not exist.");

        // Pre-attribution System outboxes still replay their original envelope (task 05).
        if (fact.ObserverId is null && fact.Target is null && stream.Source == "system" && stream.SubjectKind == SubjectKind.Machine)
            fact = fact with { ObserverId = stream.CollectorInstanceId,
                Target = new Heartbeat.Core.DTOs.Facts.FactTarget("device", stream.SubjectId.ToString("D")) };
        if (stream.Source == "browser")
        {
            if (fact.ObserverId is null && fact.Target is null && stream.SubjectKind == SubjectKind.Machine)
            {
                var observer = Heartbeat.Core.Facts.BrowserFactAttribution.Observer(stream.Dimensions.GetValueOrDefault("externalHostIdentity"));
                var target = Heartbeat.Core.Facts.BrowserFactAttribution.Target(stream.SubjectId.ToString("D"), stream.Dimensions.GetValueOrDefault("appIdentityKey"));
                if (observer is not null && target is not null) fact = fact with { ObserverId = observer, Target = target };
            }
        }
        // Same historical activity spelling boundary as Analytics; no optional Collector dispatch.
        if (fact.Time is SegmentFactTime && fact.Payload is { } payload)
        {
            try { fact = fact with { Payload = Heartbeat.Core.Facts.ActivityFactPayload.Normalize(payload) }; }
            catch (ArgumentException ex) { return Rejected(index, "fact_invalid", ex.Message); }
        }
        var envelopeError = ValidateFactEnvelope(fact);
        if (envelopeError is not null)
            return Rejected(index, "fact_invalid", envelopeError);
        var current = _state.Facts.SingleOrDefault(existing =>
            existing.StreamId == fact.StreamId && existing.FactId == fact.FactId);
        if (current is not null && fact.Revision < current.Revision)
            return new FactDeliveryOutcome(index, FactDeliveryStatus.Superseded);

        var validationError = stream.FactKind switch
        {
            FactKind.Segment => ValidateSegmentContent(fact),
            FactKind.Event => ValidateEventContent(fact, current),
            _ => "FactKind is not supported by this Collector Runtime slice."
        };
        if (validationError is not null)
            return Rejected(index, "fact_invalid", validationError);
        if (!_options.EnableFactUpload && !CanProject(stream, fact))
            return Rejected(
                index,
                "fact_invalid",
                "Fact payload is not compatible with the negotiated Hub projection shape.");
        if (!_options.EnableFactUpload && stream.FactKind == FactKind.Segment &&
            _segmentSink is not IDurableSegmentProjectionSink and not ISubjectSegmentProjectionSink)
            return Rejected(
                index,
                "fact_invalid",
                "The configured Segment projection cannot preserve durable Fact revisions.");
        if (!_options.EnableFactUpload && stream.FactKind == FactKind.Event && _inputEventSink is null)
            return Rejected(
                index,
                "fact_invalid",
                "The configured Event projection cannot preserve the existing InputEvent upload path.");

        if (current is not null)
        {
            if (fact.Revision == current.Revision)
            {
                return SameContent(current, fact)
                    ? new FactDeliveryOutcome(index, FactDeliveryStatus.Duplicate)
                    : Rejected(index, "fact_revision_conflict", "The same Fact Revision has different canonical content.");
            }

            if (stream.FactKind == FactKind.Segment &&
                (current.Start != fact.Time.Start ||
                 current.IsFinal && fact.Time.IsFinal != true))
                return Rejected(index, "fact_invalid", "Segment Revision cannot change its start or reopen a final Segment.");
        }
        CommittedFactState? evictedEvent = null;
        if (current is null)
        {
            var sameKindStreamIds = _state.Streams
                .Where(candidate => candidate.FactKind == stream.FactKind)
                .Select(candidate => candidate.StreamId)
                .ToHashSet();
            var sameKindFacts = _state.Facts
                .Where(existing => sameKindStreamIds.Contains(existing.StreamId))
                .ToArray();
            if (_options.EnableFactUpload && sameKindFacts.Count(existing =>
                    !existing.Delivered) >= _options.MaxDurableFacts)
                return Retry(index, "Hub pending Fact upload journal is applying backpressure.");
            if (sameKindFacts.Length >= _options.MaxDurableFacts)
            {
                if (!_options.EnableFactUpload && stream.FactKind != FactKind.Event)
                    return Retry(index, "Hub durable Fact inbox is applying backpressure.");
                if (_options.EnableFactUpload)
                {
                    evictedEvent = sameKindFacts.FirstOrDefault(existing =>
                        CanEvictDeliveredFact(existing));
                    if (evictedEvent is null)
                        return Retry(index, "Hub active Fact replay window is applying backpressure.");
                }
                else if (stream.FactKind == FactKind.Event)
                    evictedEvent = sameKindFacts[0];
            }
        }

        var committed = new CommittedFactState
        {
            StreamId = fact.StreamId,
            FactId = fact.FactId,
            Revision = fact.Revision,
            ObserverId = fact.ObserverId, Target = fact.Target,
            ObservedAt = fact.ObservedAt,
            Start = fact.Time.Start ?? default,
            End = fact.Time.End ?? default,
            IsFinal = fact.Time.IsFinal ?? false,
            OccurredAt = fact.Time.OccurredAt,
            Payload = fact.Payload.Clone()
        };
        // Native terminal Facts use a bounded replay window after delivery. Analytics retains
        // authoritative revision guards after pruning; the protocol ACK promises durable custody.
        // Analytics keeps revision guards when the bounded local replay window is pruned.
        prepared = new PreparedFactCommit(stream, committed, evictedEvent);
        return null;
    }

    private static bool SameContent(CommittedFactState current, FactSubmission fact) =>
        current.ObserverId == fact.ObserverId && current.Target == fact.Target &&
        current.Start == (fact.Time.Start ?? default) && current.End == (fact.Time.End ?? default) &&
        current.IsFinal == (fact.Time.IsFinal ?? false) && current.OccurredAt == fact.Time.OccurredAt &&
        current.Payload is { } payload && JsonElement.DeepEquals(payload, fact.Payload);

    private sealed record PreparedFactCommit(
        FactStreamState Stream,
        CommittedFactState Committed,
        CommittedFactState? EvictedEvent);

    private static bool ValidTarget(Heartbeat.Core.DTOs.Facts.FactTarget target)
    {
        if (target.Kind == "person")
        {
            try { _ = Heartbeat.Core.DTOs.Facts.PersonReference.Parse(target.Reference); return true; }
            catch (ArgumentException) { return false; }
        }
        if (target.Kind == "account")
        {
            try { _ = Heartbeat.Core.DTOs.Facts.ServiceAccountReference.Parse(target.Reference); return true; }
            catch (ArgumentException) { return false; }
        }
        if (target.Kind == "device") return !string.IsNullOrWhiteSpace(target.Reference) && target.Reference.Length <= 256;
        if (target.Kind != "application-context" || string.IsNullOrWhiteSpace(target.Reference) || target.Reference.Length > 8192) return false;
        try { _ = Heartbeat.Core.DTOs.Facts.ApplicationContextReference.Parse(target.Reference); return true; }
        catch (ArgumentException) { return false; }
    }

    private static string? ValidateFactEnvelope(FactSubmission fact)
    {
        if (fact.StreamId == Guid.Empty || !IsUuidV7(fact.FactId) ||
            fact.Revision is <= 0 or > MaxSafeJsonInteger)
            return "Fact identity and revisions must be UUIDv7, positive, and JSON-safe.";
        if ((fact.ObserverId is null) != (fact.Target is null) || fact.ObserverId == Guid.Empty ||
            fact.Target is { } target && (!ValidTarget(target)))
            return "Fact requires a valid Observer and Target together.";
        if (fact.ObservedAt is { Offset: var offset } && offset != TimeSpan.Zero)
            return "Fact observedAt must be UTC.";
        return null;
    }

    private static string? ValidateSegmentContent(FactSubmission fact)
    {
        if (fact.Time.Start is not { } start || fact.Time.End is not { } end ||
            fact.Time.IsFinal is null || fact.Time.OccurredAt is not null)
            return "Segment time must contain exactly start, end, and isFinal.";
        if (start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero)
            return "Segment times must be UTC.";
        if (end < start)
            return "Segment end must not precede start.";
        if (FactCanonicalization.ValidateProtocolJson(fact.Payload) is not null)
            return "Fact payload must be valid JSON.";
        return null;
    }

    private static string? ValidateEventContent(
        FactSubmission fact,
        CommittedFactState? current)
    {
        if (fact.Time.OccurredAt is not { } occurredAt ||
            fact.Time.Start is not null || fact.Time.End is not null || fact.Time.IsFinal is not null)
            return "Event time must contain exactly occurredAt.";
        if (occurredAt.Offset != TimeSpan.Zero)
            return "Event occurredAt must be UTC.";
        if (FactCanonicalization.ValidateProtocolJson(fact.Payload) is not null)
            return "Fact payload must be valid JSON.";
        if (current is not null && current.OccurredAt != occurredAt)
            return "Event Revision cannot change occurredAt.";
        return null;
    }

    private bool CanProject(FactStreamState stream, FactSubmission fact)
    {
        return stream.FactKind switch
        {
            FactKind.Segment =>
                _segmentProjector.TryProject(
                    stream,
                    fact.FactId,
                    fact.Time.Start!.Value,
                    fact.Time.End!.Value,
                    fact.Payload,
                    out _),
            FactKind.Event =>
                _inputEventProjector.TryProject(
                    fact.FactId,
                    fact.Time.OccurredAt!.Value,
                    fact.Payload,
                    out _),
            _ => false
        };
    }

    private StreamOpenPlan PlanStreams(
        Guid activationId,
        CollectorInstance instance,
        LocalCollectorPackage package,
        IReadOnlyList<OutputBinding> bindings)
    {
        if (bindings is null || bindings.Count == 0)
            throw ActivationError("output_not_declared", "Collector must open its declared Fact Streams before Ready.");
        if (bindings.Any(binding => binding is null))
            throw ActivationError("protocol_invalid_message", "streams.open bindings must not contain null.");
        if (bindings.Select(binding => binding.BindingId).Distinct(StringComparer.Ordinal).Count() != bindings.Count)
            throw ActivationError("output_not_declared", "streams.open bindingId values must be unique.");

        var normalized = new List<(OutputBinding Binding, CollectorOutputTemplate Output, Dictionary<string, string> Dimensions)>();
        foreach (var binding in bindings)
        {
            if (binding is null || binding.Dimensions is null ||
                binding.Dimensions.Any(pair => pair.Value is null))
                throw ActivationError(
                    "protocol_invalid_message",
                    "streams.open bindings and dimension values must not be null.");
            if (string.IsNullOrWhiteSpace(binding.BindingId))
                throw ActivationError("output_not_declared", "streams.open bindingId must not be empty.");
            var output = package.Manifest.Outputs.SingleOrDefault(candidate => candidate.OutputId == binding.OutputId)
                ?? throw ActivationError("output_not_declared", $"Output '{binding.OutputId}' is not declared by the Package.");
            var hasProjector = output.FactKind switch
            {
                FactKind.Segment => true,
                FactKind.Event => true,
                _ => false
            };
            if (!hasProjector)
                throw ActivationError(
                    "output_not_declared",
                    $"Output '{binding.OutputId}' has no Fact projection adapter.");
            if (!output.SubjectKinds.Contains(SubjectKindName(instance.Subject.Kind), StringComparer.Ordinal))
                throw ActivationError("output_not_declared", $"Output '{binding.OutputId}' does not support this SubjectKind.");
            if (binding.Dimensions.Keys.Any(key => !output.DimensionKeys.Contains(key, StringComparer.Ordinal)))
                throw ActivationError("output_not_declared", $"Output '{binding.OutputId}' received an undeclared dimension key.");
            var dimensions = binding.Dimensions
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Normalize(NormalizationForm.FormC),
                    StringComparer.Ordinal);
            normalized.Add((binding, output, dimensions));
        }
        if (package.Manifest.Outputs.Any(output => normalized.All(item => item.Output.OutputId != output.OutputId)))
            throw ActivationError("output_not_declared", "Collector did not open every declared Output before Ready.");

        var opened = new List<OpenedBinding>();
        var planned = new Dictionary<Guid, FactStreamState>();
        foreach (var item in normalized)
        {
            var stream = planned.Values.SingleOrDefault(candidate => StreamIdentityEquals(
                             candidate,
                             instance,
                             item.Output,
                             item.Dimensions)) ??
                         _state.Streams.SingleOrDefault(candidate => StreamIdentityEquals(
                             candidate,
                             instance,
                             item.Output,
                             item.Dimensions));
            if (stream is null)
            {
                var streamId = NextUniqueId(
                    id => _state.Streams.Any(existing => existing.StreamId == id) ||
                          planned.ContainsKey(id) ||
                          _pendingActivationCommits.Values.Any(commit =>
                              commit.Streams.Any(existing => existing.StreamId == id)),
                    "Fact Stream");
                stream = new FactStreamState
                {
                    StreamId = streamId,
                    CollectorInstanceId = instance.CollectorInstanceId,
                    SubjectId = instance.Subject.SubjectId,
                    SubjectKind = instance.Subject.Kind,
                    OutputId = item.Output.OutputId,
                    Source = item.Output.Source,
                    FactKind = item.Output.FactKind,
                    Dimensions = item.Dimensions
                };
            }
            else if (planned.TryGetValue(stream.StreamId, out var alreadyPlanned))
            {
                stream = alreadyPlanned;
            }
            if (_streamWriters.TryGetValue(stream.StreamId, out var writer) && writer != activationId)
                throw ActivationError("stream_writer_conflict", "A previous Activation still holds the Fact Stream writer lease.");
            planned[stream.StreamId] = stream;
            opened.Add(new OpenedBinding(item.Binding.BindingId, stream));
        }

        var persistedInstance = _state.Instances.Single(existing =>
            existing.CollectorInstanceId == instance.CollectorInstanceId);
        var resolvedInstance = persistedInstance with
        {
            PackageVersion = instance.PackageVersion,
            PackageContentHash = instance.PackageContentHash
        };
        return new StreamOpenPlan(
            opened,
            new PendingActivationCommit(resolvedInstance, planned.Values.ToArray()));
    }

    private static bool StreamIdentityEquals(
        FactStreamState stream,
        CollectorInstance instance,
        CollectorOutputTemplate output,
        IReadOnlyDictionary<string, string> dimensions) =>
        stream.CollectorInstanceId == instance.CollectorInstanceId &&
        stream.SubjectId == instance.Subject.SubjectId &&
        stream.SubjectKind == instance.Subject.Kind &&
        stream.OutputId == output.OutputId &&
        stream.Source == output.Source &&
        stream.FactKind == output.FactKind &&
        stream.Dimensions.Count == dimensions.Count &&
        stream.Dimensions.All(pair => dimensions.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static FactStreamDescriptor ToDescriptor(FactStreamState stream) => new(
        stream.StreamId,
        stream.CollectorInstanceId,
        new SubjectReference(stream.SubjectId, stream.SubjectKind),
        stream.OutputId,
        stream.Source,
        stream.FactKind,
        stream.Dimensions.ToImmutableDictionary(StringComparer.Ordinal));

    private CollectorInstanceState GetInstanceStateLocked(Guid collectorInstanceId)
    {
        return _state.Instances.SingleOrDefault(instance => instance.CollectorInstanceId == collectorInstanceId)
            ?? throw ActivationError("instance_not_found", $"Collector Instance '{collectorInstanceId}' was not found.");
    }

    private void ValidatePackageCandidate(
        CollectorInstanceState instance,
        LocalCollectorPackage package)
    {
        if (instance.PackageId != package.Manifest.PackageId)
            throw ActivationError(
                "package_mismatch",
                "Collector Instance is permanently bound to its PackageId.");
        if (!package.Manifest.Config.AcceptedVersions.Contains(instance.ConfigVersion))
            throw ActivationError(
                "config_version_unsupported",
                $"Collector Package '{package.Manifest.PackageId}/{package.Manifest.Version}' does not accept ConfigVersion {instance.ConfigVersion}.");
    }

    private static VerifiedCollectorArtifact ResolveProtocolArtifact(
        LocalCollectorPackage package,
        string artifactId,
        string executionDriver)
    {
        var artifact = ResolveProtocolArtifact(package, executionDriver);
        if (artifact.ArtifactId != artifactId)
            throw ActivationError("package_mismatch", $"Artifact '{artifactId}' is not the selected current {executionDriver} target.");
        return artifact;
    }

    private static VerifiedCollectorArtifact ResolveProtocolArtifact(
        LocalCollectorPackage package,
        string executionDriver)
    {
        var operatingSystem = CurrentOperatingSystem();
        var architecture = CurrentArchitecture();
        var candidates = package.Manifest.Artifacts.Where(artifact =>
            artifact.Driver == executionDriver &&
            artifact.OperatingSystems.Contains(operatingSystem, StringComparer.Ordinal) &&
            artifact.Architectures.Contains(architecture, StringComparer.Ordinal)).ToArray();
        if (candidates.Length != 1)
            throw ActivationError(
                "package_mismatch",
                $"Collector Package must have exactly one Artifact for {executionDriver}/{operatingSystem}/{architecture}; found {candidates.Length}.");
        return package.Artifacts.Single(artifact => artifact.ArtifactId == candidates[0].ArtifactId);
    }

    private void ValidateProtocolSupport(LocalCollectorPackage package, ProtocolSupport? support)
    {
        if (support?.ProtocolMajors is null || support.Capabilities is null ||
            support.ProtocolMajors.Count == 0 ||
            support.ProtocolMajors.Any(major => major <= 0) ||
            support.ProtocolMajors.Distinct().Count() != support.ProtocolMajors.Count ||
            support.Capabilities.Any(capability =>
                string.IsNullOrWhiteSpace(capability.Key) || capability.Value is null ||
                capability.Value.Count == 0 || capability.Value.Any(version => version <= 0) ||
                capability.Value.Distinct().Count() != capability.Value.Count))
            throw ActivationError(
                "protocol_invalid_message",
                "activation.hello protocolMajors and supportedCapabilities are malformed.");
        if (!support.ProtocolMajors.Contains(1) || !package.Manifest.ProtocolMajors.Contains(1))
            throw ActivationError("protocol_no_common_major", "No common Collector Protocol major.");

        foreach (var capability in package.Manifest.Outputs.Select(output => output.FactKind switch
                 {
                     FactKind.Segment => "facts.segment",
                     FactKind.Event => "facts.event",
                     FactKind.Measurement => "facts.measurement.gauge",
                     _ => string.Empty
                 }).Append("diagnostics.stream-gap"))
        {
            if (capability == "facts.event" && !_options.EnableFactUpload && _inputEventSink is null ||
                !HubProtocolCapabilities.TryGetValue(capability, out var hubVersions) ||
                !package.Manifest.SupportedCapabilities.TryGetValue(capability, out var packageVersions) ||
                !support.Capabilities.TryGetValue(capability, out var collectorVersions) ||
                !hubVersions.Intersect(packageVersions).Intersect(collectorVersions).Any())
                throw ActivationError(
                    "capability_no_common_version",
                    $"Required protocol capability '{capability}' has no common version.");
        }
    }

    private static ProtocolSupport? SnapshotProtocolSupport(ProtocolSupport? support)
    {
        if (support is null)
            return null;

        var protocolMajors = support.ProtocolMajors?.ToImmutableArray();
        if (support.Capabilities is null)
            return new ProtocolSupport(protocolMajors!, null!);

        var capabilities = ImmutableDictionary.CreateBuilder<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var capability in support.Capabilities)
            capabilities.Add(
                capability.Key,
                capability.Value is null ? null! : capability.Value.ToImmutableArray());
        return new ProtocolSupport(protocolMajors!, capabilities.ToImmutable());
    }

    private static InProcessCollectorInitialization SnapshotInitialization(
        InProcessCollectorInitialization initialized)
    {
        if (initialized.Bindings is null)
            throw new InvalidOperationException("activation.initialized bindings must be present.");

        var bindings = ImmutableArray.CreateBuilder<OutputBinding>();
        foreach (var binding in initialized.Bindings)
        {
            if (binding is null)
            {
                bindings.Add(null!);
                continue;
            }
            if (binding.Dimensions is null)
            {
                bindings.Add(binding with { Dimensions = null! });
                continue;
            }

            var dimensions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var dimension in binding.Dimensions)
            {
                if (!dimensions.TryAdd(dimension.Key, dimension.Value))
                    throw new InvalidOperationException(
                        $"activation.initialized binding '{binding.BindingId}' contains duplicate dimensions.");
            }
            bindings.Add(new OutputBinding(
                binding.BindingId,
                binding.OutputId,
                dimensions.ToImmutable()));
        }
        return new InProcessCollectorInitialization(
            initialized.AppliedSpecRevision,
            bindings.ToImmutable());
    }

    private Guid NextUniqueId(Func<Guid, bool> exists, string kind)
    {
        var id = _options.IdGenerator();
        if (!IsUuidV7(id) || exists(id))
            throw new InvalidOperationException($"Collector Runtime generated an invalid or duplicate UUIDv7 {kind} ID.");
        return id;
    }

    private CollectorActivationSession CreateActivationSession(
        Guid activationId,
        Guid helloMessageId,
        LocalCollectorPackage package,
        ActivationDeliveryCapability deliveryCapability)
    {
        var deliveryFence = new ActivationDeliveryFence();
        return new CollectorActivationSession(
            activationId,
            helloMessageId,
            package,
            new CollectorProtocolLimits(_options.MaxFactsPerBatch, _options.MaxBatchBytes),
            deliveryCapability,
            deliveryFence,
            (streamId, facts) => CommitFacts(activationId, streamId, facts, deliveryFence),
            (streamId, gap) => CommitGap(activationId, streamId, gap, deliveryFence),
            MarkAcknowledgedLiveTraffic);
    }

    private static string SubjectKindName(SubjectKind kind) => kind switch
    {
        SubjectKind.Machine => "machine",
        SubjectKind.Account => "account",
        SubjectKind.Person => "person",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string CurrentOperatingSystem() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        throw ActivationError("package_mismatch", "Current operating system is not supported by Collector Protocol v1.");

    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw ActivationError("package_mismatch", "Current architecture is not supported by Collector Protocol v1.")
    };

    private static CollectorActivationException ActivationError(
        string code,
        string message,
        Exception? exception = null,
        bool retryable = false) => new(
        new CollectorProtocolError(code, message, retryable),
        exception);

    private static FactDeliveryOutcome Rejected(int index, string code, string message) => new(
        index,
        FactDeliveryStatus.Rejected,
        new CollectorProtocolError(code, message, false));

    private FactDeliveryOutcome Retry(int index, string message) => new(
        index,
        FactDeliveryStatus.Retry,
        new CollectorProtocolError("hub_backpressure", message, true),
        _options.RetryAfterMilliseconds);

    private static GapDeliveryOutcome GapRejected(Guid streamId, string code, string message) => new(
        streamId,
        GapDeliveryStatus.Rejected,
        new CollectorProtocolError(code, message, false));

    private GapDeliveryOutcome GapRetry(Guid streamId, string message) => new(
        streamId,
        GapDeliveryStatus.Retry,
        new CollectorProtocolError("hub_backpressure", message, true),
        _options.RetryAfterMilliseconds);

    private static FactBatchAcknowledgement MessageRejected(string code, string message) => new(
        [],
        new CollectorProtocolError(code, message, false));

    private static string HelloRequestHash(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        string? artifactId,
        ProtocolSupport? support)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("collectorInstanceId", collectorInstanceId);
            writer.WriteString("packageId", package.Manifest.PackageId);
            writer.WriteString("packageVersion", package.Manifest.Version);
            writer.WriteString("packageContentHash", package.PackageContentHash);
            writer.WriteString("artifactId", artifactId);
            writer.WritePropertyName("protocolSupport");
            if (support is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("protocolMajors");
                if (support.ProtocolMajors is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartArray();
                    foreach (var major in support.ProtocolMajors.Order())
                        writer.WriteNumberValue(major);
                    writer.WriteEndArray();
                }
                writer.WritePropertyName("capabilities");
                if (support.Capabilities is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartObject();
                    foreach (var capability in support.Capabilities.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(capability.Key);
                        if (capability.Value is null)
                        {
                            writer.WriteNullValue();
                        }
                        else
                        {
                            writer.WriteStartArray();
                            foreach (var version in capability.Value.Order())
                                writer.WriteNumberValue(version);
                            writer.WriteEndArray();
                        }
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private void PersistActivationAttemptTombstoneLocked(
        Guid collectorInstanceId,
        Guid helloMessageId,
        string requestHash,
        Guid activationId)
    {
        var previous = _state.ActivationAttemptTombstones.SingleOrDefault(attempt =>
            attempt.CollectorInstanceId == collectorInstanceId &&
            attempt.MessageId == helloMessageId);
        if (previous is not null)
        {
            if (previous.RequestHash != requestHash)
                throw ActivationError(
                    "protocol_invalid_message",
                    "The same activation.hello messageId was reused with different content.");
            throw ActivationError(
                "protocol_invalid_message",
                "The activation.hello attempt belongs to a previous Runtime session; send a new messageId.");
        }
        if (_state.ActivationAttemptTombstones.Any(attempt => attempt.ActivationId == activationId))
            throw ActivationError(
                "protocol_invalid_message",
                "The Collector Activation identifier belongs to a previous Runtime session.");

        var next = _state.WithActivationAttemptTombstone(new ActivationAttemptTombstoneState
        {
            CollectorInstanceId = collectorInstanceId,
            MessageId = helloMessageId,
            RequestHash = requestHash,
            ActivationId = activationId
        });
        try
        {
            _store.Save(next);
            _state = next;
        }
        catch (CollectorRuntimeStateException exception)
        {
            throw ActivationError(
                "hub_backpressure",
                "Hub could not persist activation.hello attempt identity.",
                exception,
                retryable: true);
        }
    }

    private void ReplayCommittedFacts()
    {
        lock (_gate)
        {
            if (_options.EnableFactUpload)
            {
                foreach (var fact in _state.Facts)
                    ObserveCommittedFact(_state.Streams.Single(stream => stream.StreamId == fact.StreamId), fact);
                return;
            }
            var replaySink = _inputEventSink as IInputEventFactReplaySink;
            List<InputEventItem>? replayEvents = replaySink is null ? null : [];
            foreach (var fact in _state.Facts)
            {
                var stream = _state.Streams.SingleOrDefault(candidate => candidate.StreamId == fact.StreamId);
                if (stream is null)
                    continue;
                if (stream.FactKind == FactKind.Event && replayEvents is not null)
                {
                    if (TryCreateInputEventProjection(stream, fact, out var item))
                        replayEvents.Add(item!);
                    continue;
                }
                ProjectFact(stream, fact, isReplay: true);
            }

            if (replayEvents is { Count: > 0 })
            {
                try
                {
                    replaySink!.Replay(replayEvents);
                }
                catch (Exception exception)
                {
                    Log.Error(
                        exception,
                        "已持久接收 {Count} 条 Collector Event Fact，批量投影到 InputEvent 上传缓冲失败；重启时将重放",
                        replayEvents.Count);
                }
            }
        }
    }

    private void ProjectFact(FactStreamState stream, CommittedFactState fact, bool isReplay)
    {
        switch (stream.FactKind)
        {
            case FactKind.Segment:
                ProjectSegment(stream, fact, isReplay);
                break;
            case FactKind.Event:
                ProjectEvent(stream, fact, isReplay);
                break;
        }
    }

    private void ProjectSegment(FactStreamState stream, CommittedFactState fact, bool isReplay)
    {
        if (fact.Payload is not { } payload ||
            !_segmentProjector.TryProject(
                stream,
                fact.FactId,
                fact.Start,
                fact.End,
                payload,
                out var item))
        {
            Log.Error(
                "已持久接收 Collector Segment Fact {FactId}，但其 payload 无法由 业务投影 投影",
                fact.FactId);
            return;
        }
        try
        {
            if (_segmentSink is ISubjectSegmentProjectionSink subjectSink)
            {
                var context = ContextForStream(stream);
                if (isReplay)
                    subjectSink.ReplayDurable(context, item!, fact.Revision, fact.IsFinal);
                else
                    subjectSink.UpsertDurable(context, item!, fact.Revision, fact.IsFinal);
            }
            else if (_segmentSink is IDurableSegmentProjectionSink durableSink)
            {
                if (isReplay)
                    durableSink.ReplayDurable(item!, fact.Revision);
                else
                    durableSink.UpsertDurable(item!, fact.Revision);
            }
            else
                Log.Error(
                    "已持久接收 Collector Segment Fact {FactId}，但投影 sink 不支持 durable revision",
                    fact.FactId);
        }
        catch (Exception exception)
        {
            Log.Error(
                exception,
                "已持久接收 Collector Segment Fact {FactId}，投影到 Hub 缓冲失败；重启时将重放",
                fact.FactId);
        }
    }

    private CollectorProjectionContext ContextForStream(FactStreamState stream)
    {
        var instance = _state.Instances.Single(candidate =>
            candidate.CollectorInstanceId == stream.CollectorInstanceId);
        return new CollectorProjectionContext(
            stream.CollectorInstanceId,
            new SubjectReference(instance.SubjectId, instance.SubjectKind));
    }

    private bool ProjectEvent(
        FactStreamState stream,
        CommittedFactState fact,
        bool isReplay,
        ICollectorProjectionCommitFence? commitFence = null)
    {
        if (!TryCreateInputEventProjection(stream, fact, out var item))
            return false;
        if (_inputEventSink is null)
        {
            Log.Error(
                "已持久接收 Collector Event Fact {FactId}，但未配置 InputEvent 投影 sink",
                fact.FactId);
            return false;
        }
        try
        {
            return _inputEventSink.TryAccept(
                item!,
                isReplay,
                commitFence ?? UnfencedCollectorProjectionCommitFence.Instance);
        }
        catch (Exception exception)
        {
            Log.Error(
                exception,
                "已持久接收 Collector Event Fact {FactId}，投影到 InputEvent 上传缓冲失败；重启时将重放",
                fact.FactId);
            return false;
        }
    }

    private bool TryCreateInputEventProjection(
        FactStreamState stream,
        CommittedFactState fact,
        out InputEventItem? item)
    {
        item = null;
        if (fact.OccurredAt is { } occurredAt &&
            fact.Payload is { } payload &&
            _inputEventProjector.TryProject(fact.FactId, occurredAt, payload, out item))
            return true;

        Log.Error(
            "已持久接收 Collector Event Fact {FactId}，但其 payload 无法由 业务投影 投影",
            fact.FactId);
        return false;
    }

    private void MarkAcknowledgedLiveTraffic(
        Guid streamId,
        IReadOnlyList<FactDeliveryOutcome> outcomes)
    {
        if (!outcomes.Any(outcome => outcome.IsAcknowledged) ||
            _segmentSink is not ICollectorTrafficSink trafficSink)
            return;
        string? source;
        lock (_gate)
            source = _state.Streams.SingleOrDefault(candidate => candidate.StreamId == streamId)?.Source;
        if (source is null)
            return;
        try
        {
            trafficSink.MarkSourceActive(source);
        }
        catch (Exception exception)
        {
            Log.Error(
                exception,
                "Collector Source {Source} 的实时流量盖戳失败；Fact ACK 与 durable inbox 保持有效",
                source);
        }
    }


    private sealed record OpenedBinding(string BindingId, FactStreamState Stream);
    private sealed record StreamOpenPlan(
        IReadOnlyList<OpenedBinding> Bindings,
        PendingActivationCommit Commit);
    private sealed record PendingActivationCommit(
        CollectorInstanceState Instance,
        IReadOnlyList<FactStreamState> Streams);
    private sealed record HelloAttempt(
        string RequestHash,
        TaskCompletionSource<InProcessCollectorActivation> Completion);
}
