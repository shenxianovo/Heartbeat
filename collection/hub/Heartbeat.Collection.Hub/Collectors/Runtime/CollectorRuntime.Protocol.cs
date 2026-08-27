using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Segments;
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
    private readonly Dictionary<Guid, PendingPackageFingerprint> _pendingPackageFingerprints = [];
    private readonly Dictionary<string, FactSchemaDocument> _factSchemasByHash = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<ISegmentFactProjector> _segmentProjectors;
    private readonly IReadOnlyList<IEventFactProjector> _eventProjectors;
    private readonly Dictionary<Guid, Guid> _streamWriters = [];
    private readonly HashSet<Guid> _startingInstances = [];
    private readonly Dictionary<Guid, StartingCollector> _startingCollectors = [];
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
        CancellationToken cancellationToken)
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
        var collectorInitializationStarted = false;
        StartingCollector? startingCollector = null;
        InProcessCollectorActivation? activation = null;
        CollectorActivationSession? session = null;
        Guid? packageReservationActivationId = null;

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
                    _activations.Values.Any(activation =>
                        activation.State != CollectorActivationState.Stopped &&
                        activation.Streams.Values.Any(stream =>
                            stream.Descriptor.CollectorInstanceId == collectorInstanceId)))
                    throw ActivationError(
                        "stream_writer_conflict",
                        "Stop the current Collector Activation before starting its replacement.");
                _startingInstances.Add(collectorInstanceId);
                registeredStartingInstance = true;
                startingCollector = new StartingCollector(collectorInstanceId, collector);
                _startingCollectors.Add(collectorInstanceId, startingCollector);
                _pendingPackageFingerprints.Add(
                    activationId,
                    new PendingPackageFingerprint(
                        package.Manifest.PackageId,
                        package.Manifest.Version,
                        package.PackageContentHash));
                packageReservationActivationId = activationId;
                session = CreateActivationSession(
                    activationId,
                    helloMessageId,
                    package,
                    ActivationDeliveryCapability.Complete);
            }

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
                    collectorInstanceId.ToString("N"))));
            InProcessCollectorInitialization initialized;
            try
            {
                collectorInitializationStarted = true;
                initialized = await collector.InitializeAsync(initialization, cancellationToken);
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

            cancellationToken.ThrowIfCancellationRequested();
            InProcessCollectorStreamsOpened opened;
            lock (_gate)
            {
                ThrowIfDisposed();
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
                    this,
                    session,
                    collector,
                    streams);
                _activations.Add(activationId, activation);
                _pendingActivationCommits.Add(activationId, streamPlan.Commit);
                startingCollector!.AttachActivation(activation);
                opened = new InProcessCollectorStreamsOpened(
                    activationId,
                    streams.ToImmutableDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Descriptor,
                        StringComparer.Ordinal),
                    readyCancellationToken => CompleteCollectorReady(activation, readyCancellationToken));
            }

            try
            {
                await startingCollector!.InvokeStreamsOpenedAsync(
                    () => collector.OnStreamsOpenedAsync(opened, cancellationToken));
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
                startingCollector.MarkActivationCompleted();
                if (_startingCollectors.TryGetValue(collectorInstanceId, out var registered) &&
                    ReferenceEquals(registered, startingCollector))
                    _startingCollectors.Remove(collectorInstanceId);
                registeredStartingInstance = false;
            }
            ownedHelloAttempt.Completion.SetResult(activation);
            return activation;
        }
        catch (Exception exception)
        {
            var cleanupCompleted = true;
            if (activation is not null && activation.State != CollectorActivationState.Stopped)
            {
                try
                {
                    await activation.StopAsync(CancellationToken.None);
                }
                catch (Exception stopException)
                {
                    cleanupCompleted = false;
                    Log.Warning(
                        stopException,
                        "停止失败的 InProcess Collector Activation {ActivationId} 时发生异常",
                        activation.ActivationId);
                }
            }
            else if (activation is null && collectorInitializationStarted && startingCollector is not null)
            {
                try
                {
                    await startingCollector.StopAsync();
                }
                catch (Exception stopException)
                {
                    cleanupCompleted = false;
                    Log.Warning(
                        stopException,
                        "停止初始化失败的 InProcess Collector 时发生异常");
                }
            }
            if (registeredStartingInstance && cleanupCompleted)
            {
                lock (_gate)
                {
                    _startingInstances.Remove(collectorInstanceId);
                    startingCollector?.MarkActivationCompleted();
                    if (startingCollector is not null &&
                        _startingCollectors.TryGetValue(collectorInstanceId, out var registered) &&
                        ReferenceEquals(registered, startingCollector))
                        _startingCollectors.Remove(collectorInstanceId);
                    if (packageReservationActivationId is { } reservedActivationId)
                        _pendingPackageFingerprints.Remove(reservedActivationId);
                }
            }
            startingCollector?.MarkActivationCompleted();
            ownedHelloAttempt.Completion.TrySetException(exception);
            _ = ownedHelloAttempt.Completion.Task.Exception;
            throw;
        }
    }

    private FactBatchAcknowledgement CommitFacts(
        Guid activationId,
        Guid streamId,
        IReadOnlyList<FactSubmission> facts)
    {
        lock (_gate)
        {
            ThrowIfDeliveryUnavailable(activationId);
            var results = new List<FactDeliveryOutcome>(facts.Count);
            for (var index = 0; index < facts.Count; index++)
                results.Add(CommitFact(activationId, index, facts[index]));
            MarkAcknowledgedLiveTraffic(streamId, results);
            return new FactBatchAcknowledgement(results);
        }
    }

    internal void CompleteStop(InProcessCollectorActivation activation)
    {
        Guid collectorInstanceId;
        lock (_gate)
        {
            foreach (var streamId in activation.Streams.Values.Select(stream => stream.Descriptor.StreamId))
            {
                if (_streamWriters.TryGetValue(streamId, out var writer) && writer == activation.ActivationId)
                    _streamWriters.Remove(streamId);
            }
            _activations.Remove(activation.ActivationId);
            _pendingActivationCommits.Remove(activation.ActivationId);
            _pendingPackageFingerprints.Remove(activation.ActivationId);
            collectorInstanceId = activation.Streams.Values
                .Select(stream => stream.Descriptor.CollectorInstanceId)
                .FirstOrDefault();
        }
        if (collectorInstanceId != Guid.Empty)
        {
            lock (_helloAttemptGate)
                _helloAttempts.Remove((collectorInstanceId, activation.HelloMessageId));
        }
    }

    private InProcessCollectorActivation CompleteCollectorReady(
        InProcessCollectorActivation activation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        activation.Session.AcceptReady(
            expectedSpecRevision,
            expectedSpecRevision,
            () => CommitCollectorReady(activation));
        return activation;
    }

    private void CommitCollectorReady(InProcessCollectorActivation activation)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var stream in activation.Streams.Values)
            {
                if (_streamWriters.TryGetValue(stream.Descriptor.StreamId, out var writer) &&
                    writer != activation.ActivationId)
                    throw ActivationError(
                        "stream_writer_conflict",
                        "A previous Activation still holds the Fact Stream writer lease.");
            }
            if (!_pendingActivationCommits.TryGetValue(activation.ActivationId, out var pendingCommit))
                throw ActivationError(
                    "protocol_invalid_message",
                    "Collector Activation has no pending Package and Stream state to commit.");
            var next = _state.WithInstanceAndStreams(
                pendingCommit.Instance,
                pendingCommit.Streams);
            try
            {
                _store.Save(next);
            }
            catch (CollectorRuntimeStateException exception)
            {
                throw ActivationError(
                    "hub_backpressure",
                    "Hub could not persist the resolved Package and Fact Streams.",
                    exception,
                    retryable: true);
            }
            _state = next;
            _pendingActivationCommits.Remove(activation.ActivationId);
            _pendingPackageFingerprints.Remove(activation.ActivationId);
            foreach (var schema in activation.Package.FactSchemas)
                _factSchemasByHash[schema.ContentHash] = schema;
            foreach (var stream in activation.Streams.Values)
                _streamWriters[stream.Descriptor.StreamId] = activation.ActivationId;
        }
    }

    private GapDeliveryOutcome CommitGap(
        Guid activationId,
        Guid streamId,
        StreamGapReport gap)
    {
        lock (_gate)
        {
            ThrowIfDeliveryUnavailable(activationId);
            if (!_streamWriters.TryGetValue(streamId, out var activeWriter) || activeWriter != activationId)
                return GapRejected(
                    streamId,
                    "stream_writer_conflict",
                    "Activation does not hold this Fact Stream writer lease.");

            GapDeliveryOutcome outcome;
            if (_state.Gaps.Any(existing =>
                         existing.StreamId == streamId && existing.Start == gap.Start &&
                         existing.End == gap.End && existing.Reason == gap.Reason))
            {
                outcome = new GapDeliveryOutcome(streamId, GapDeliveryStatus.Duplicate);
            }
            else if (_state.Gaps.Count >= _options.MaxDurableFacts)
            {
                outcome = GapRetry(streamId, "Hub durable Gap inbox is applying backpressure.");
            }
            else
            {
                var committed = new CommittedGapState
                {
                    StreamId = streamId,
                    Start = gap.Start,
                    End = gap.End,
                    Reason = gap.Reason,
                    EstimatedFactsLost = gap.EstimatedFactsLost
                };
                var next = _state.WithGap(committed);
                try
                {
                    _store.Save(next);
                    _state = next;
                    outcome = new GapDeliveryOutcome(streamId, GapDeliveryStatus.Committed);
                }
                catch (CollectorRuntimeStateException)
                {
                    outcome = GapRetry(streamId, "Hub could not persist the Stream Gap and is applying backpressure.");
                }
            }

            return outcome;
        }
    }

    private FactDeliveryOutcome CommitFact(Guid activationId, int index, FactSubmission fact)
    {
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
            return Rejected(index, "fact_schema_invalid", "Fact Stream does not exist.");

        var envelopeError = ValidateFactEnvelope(fact);
        if (envelopeError is not null)
            return Rejected(index, "fact_schema_invalid", envelopeError);
        var current = _state.Facts.SingleOrDefault(existing =>
            existing.StreamId == fact.StreamId && existing.FactId == fact.FactId);
        if (current is not null && fact.Revision < current.Revision)
            return new FactDeliveryOutcome(index, FactDeliveryStatus.Superseded);

        if (!stream.SchemaCatalog.TryGetValue(fact.SchemaRevision, out var expectedSchemaHash) ||
            !_factSchemasByHash.TryGetValue(expectedSchemaHash, out var schema) ||
            schema.SchemaId != stream.SchemaId ||
            schema.SchemaMajor != stream.SchemaMajor ||
            schema.SchemaRevision != fact.SchemaRevision ||
            schema.FactKind != stream.FactKind)
            return Rejected(index, "fact_schema_invalid", "Fact Schema revision is not available for this Stream.");

        var validationError = stream.FactKind switch
        {
            FactKind.Segment => ValidateSegmentContent(fact, schema),
            FactKind.Event => ValidateEventContent(fact, schema, current),
            _ => "FactKind is not supported by this Collector Runtime slice."
        };
        if (validationError is not null)
            return Rejected(index, "fact_schema_invalid", validationError);
        if (!CanProject(stream, fact))
            return Rejected(
                index,
                "fact_schema_invalid",
                "Fact Schema has no compatible projection adapter for the existing Hub buffer.");
        if (stream.FactKind == FactKind.Segment && _segmentSink is not IDurableSegmentProjectionSink)
            return Rejected(
                index,
                "fact_schema_invalid",
                "The configured Segment projection cannot preserve durable Fact revisions.");
        if (stream.FactKind == FactKind.Event && _inputEventSink is null)
            return Rejected(
                index,
                "fact_schema_invalid",
                "The configured Event projection cannot preserve the existing InputEvent upload path.");

        var contentHash = FactCanonicalization.ContentHash(fact);
        if (current is not null)
        {
            if (fact.Revision == current.Revision)
            {
                return current.ContentHash == contentHash
                    ? new FactDeliveryOutcome(index, FactDeliveryStatus.Duplicate)
                    : Rejected(index, "fact_revision_conflict", "The same Fact Revision has different canonical content.");
            }

            if (stream.FactKind == FactKind.Segment &&
                (current.Start != fact.Time.Start ||
                 current.RecordState == FactRecordState.Retracted && fact.RecordState == FactRecordState.Present ||
                 current.IsFinal && fact.Time.IsFinal != true))
                return Rejected(index, "fact_schema_invalid", "Segment Revision violates its evolution rules.");
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
            if (sameKindFacts.Length >= _options.MaxDurableFacts)
            {
                if (stream.FactKind != FactKind.Event)
                    return Retry(index, "Hub durable Fact inbox is applying backpressure.");
                evictedEvent = sameKindFacts[0];
            }
        }

        var committed = new CommittedFactState
        {
            StreamId = fact.StreamId,
            FactId = fact.FactId,
            SchemaRevision = fact.SchemaRevision,
            Revision = fact.Revision,
            RecordState = fact.RecordState,
            ObservedAt = fact.ObservedAt,
            Start = fact.Time.Start ?? default,
            End = fact.Time.End ?? default,
            IsFinal = fact.Time.IsFinal ?? false,
            OccurredAt = fact.Time.OccurredAt,
            Payload = fact.RecordState == FactRecordState.Present ? fact.Payload.Clone() : null,
            ContentHash = contentHash
        };
        // Immutable Events use the durable inbox as a bounded replay/deduplication window. Their
        // projected InputEvent IDs remain stable downstream, so advancing this window keeps a raw
        // production stream flowing without weakening ACK-loss idempotency for retained entries.
        var next = _state.WithFact(committed, evictedEvent);
        if (stream.FactKind == FactKind.Event && !ProjectEvent(stream, committed, isReplay: false))
            return Retry(index, "Hub durable Event projection is applying backpressure.");
        try
        {
            _store.Save(next);
        }
        catch (CollectorRuntimeStateException)
        {
            return Retry(index, "Hub could not persist the Fact and is applying backpressure.");
        }

        _state = next;
        if (stream.FactKind != FactKind.Event)
            ProjectFact(stream, committed, isReplay: false);
        return new FactDeliveryOutcome(index, FactDeliveryStatus.Committed);
    }

    private static string? ValidateFactEnvelope(FactSubmission fact)
    {
        if (fact.StreamId == Guid.Empty || !IsUuidV7(fact.FactId) || fact.SchemaRevision <= 0 ||
            fact.Revision is <= 0 or > MaxSafeJsonInteger)
            return "Fact identity and revisions must be UUIDv7, positive, and JSON-safe.";
        if (!Enum.IsDefined(fact.RecordState))
            return "Fact recordState is not defined by Collector Protocol v1.";
        if (fact.ObservedAt is { Offset: var offset } && offset != TimeSpan.Zero)
            return "Fact observedAt must be UTC.";
        return null;
    }

    private static string? ValidateSegmentContent(FactSubmission fact, FactSchemaDocument schema)
    {
        if (fact.Time.Start is not { } start || fact.Time.End is not { } end ||
            fact.Time.IsFinal is null || fact.Time.OccurredAt is not null)
            return "Segment time must contain exactly start, end, and isFinal.";
        if (start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero)
            return "Segment times must be UTC.";
        if (end < start)
            return "Segment end must not precede start.";
        if (fact.RecordState == FactRecordState.Retracted)
        {
            if (fact.Revision == 1)
                return "Retracted Fact must use a Revision higher than 1.";
            if (fact.Payload.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                return "Retracted Fact must omit payload.";
            return schema.AllowRetraction ? null : "Fact Schema does not allow retraction.";
        }
        if (fact.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined || !schema.IsPayloadValid(fact.Payload))
            return "Fact payload does not satisfy its Fact Schema Document.";
        return null;
    }

    private static string? ValidateEventContent(
        FactSubmission fact,
        FactSchemaDocument schema,
        CommittedFactState? current)
    {
        if (fact.Time.OccurredAt is not { } occurredAt ||
            fact.Time.Start is not null || fact.Time.End is not null || fact.Time.IsFinal is not null)
            return "Event time must contain exactly occurredAt.";
        if (occurredAt.Offset != TimeSpan.Zero)
            return "Event occurredAt must be UTC.";
        if (current is null && fact.Revision != 1)
            return "An Event must first be submitted at Revision 1.";
        if (fact.RecordState == FactRecordState.Retracted)
        {
            if (fact.Revision == 1)
                return "Retracted Fact must use a Revision higher than 1.";
            if (fact.Payload.ValueKind != JsonValueKind.Undefined)
                return "Retracted Fact must omit payload.";
            return schema.AllowRetraction ? null : "Fact Schema does not allow retraction.";
        }
        if (fact.Payload.ValueKind == JsonValueKind.Undefined || !schema.IsPayloadValid(fact.Payload))
            return "Fact payload does not satisfy its Fact Schema Document.";
        if (current is not null && current.OccurredAt != occurredAt)
            return "Event Revision cannot change occurredAt.";
        if (current is not null && fact.Revision > current.Revision &&
            schema.EvolutionMode != FactEvolutionMode.MutableEvent)
            return "Immutable Event Fact Schema does not allow a higher present Revision.";
        return null;
    }

    private bool CanProject(FactStreamState stream, FactSubmission fact)
    {
        if (fact.RecordState == FactRecordState.Retracted)
            return stream.FactKind == FactKind.Segment;
        return stream.FactKind switch
        {
            FactKind.Segment =>
                ResolveSegmentProjector(stream.SchemaId, stream.SchemaMajor) is { } segmentProjector &&
                segmentProjector.TryProject(
                    stream,
                    fact.FactId,
                    fact.Time.Start!.Value,
                    fact.Time.End!.Value,
                    fact.Time.IsFinal == true,
                    fact.Payload,
                    out _),
            FactKind.Event =>
                ResolveEventProjector(stream.SchemaId, stream.SchemaMajor) is { } eventProjector &&
                eventProjector.TryProject(
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
                FactKind.Segment => ResolveSegmentProjector(output.Schema.Id, output.Schema.Major) is not null,
                FactKind.Event => ResolveEventProjector(output.Schema.Id, output.Schema.Major) is not null,
                _ => false
            };
            if (!hasProjector)
                throw ActivationError(
                    "output_not_declared",
                    $"Output '{binding.OutputId}' has no registered Fact projection adapter for " +
                    $"schema '{output.Schema.Id}/{output.Schema.Major}'.");
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
        ValidatePackageSchemaIdentities(package);

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
                var packageSchemas = PackageSchemas(package, item.Output).ToArray();
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
                    SchemaId = item.Output.Schema.Id,
                    SchemaMajor = item.Output.Schema.Major,
                    SchemaRevision = item.Output.Schema.Revision,
                    SchemaHash = item.Output.Schema.Hash,
                    SchemaCatalog = packageSchemas.ToDictionary(
                        schema => schema.SchemaRevision,
                        schema => schema.ContentHash),
                    SchemaDocuments = packageSchemas.ToDictionary(
                        schema => schema.SchemaRevision,
                        schema => schema.Content.ToArray()),
                    Dimensions = item.Dimensions
                };
            }
            else if (!planned.TryGetValue(stream.StreamId, out var alreadyPlanned))
            {
                stream = ResolveStreamForPackage(stream, package, item.Output);
            }
            else
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
        var packageFingerprints = new Dictionary<string, string>(
            persistedInstance.PackageFingerprints,
            StringComparer.Ordinal)
        {
            [instance.PackageVersion] = instance.PackageContentHash
        };
        var resolvedInstance = persistedInstance with
        {
            PackageVersion = instance.PackageVersion,
            PackageContentHash = instance.PackageContentHash,
            PackageFingerprints = packageFingerprints
        };
        return new StreamOpenPlan(
            opened,
            new PendingActivationCommit(resolvedInstance, planned.Values.ToArray()));
    }

    private static FactStreamState ResolveStreamForPackage(
        FactStreamState stream,
        LocalCollectorPackage package,
        CollectorOutputTemplate output)
    {
        var schemaCatalog = new Dictionary<int, string>(stream.SchemaCatalog);
        var schemaDocuments = stream.SchemaDocuments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray());
        foreach (var schema in PackageSchemas(package, output))
        {
            if (schemaCatalog.TryGetValue(schema.SchemaRevision, out var existingHash) &&
                existingHash != schema.ContentHash)
                throw ActivationError(
                    "package_mismatch",
                    $"Fact Schema '{output.Schema.Id}/{output.Schema.Major}/{schema.SchemaRevision}' changed content hash across Package versions.");
            schemaCatalog[schema.SchemaRevision] = schema.ContentHash;
            schemaDocuments[schema.SchemaRevision] = schema.Content.ToArray();
        }
        return new FactStreamState
        {
            StreamId = stream.StreamId,
            CollectorInstanceId = stream.CollectorInstanceId,
            SubjectId = stream.SubjectId,
            SubjectKind = stream.SubjectKind,
            OutputId = stream.OutputId,
            Source = stream.Source,
            FactKind = stream.FactKind,
            SchemaId = stream.SchemaId,
            SchemaMajor = stream.SchemaMajor,
            SchemaRevision = output.Schema.Revision,
            SchemaHash = output.Schema.Hash,
            SchemaCatalog = schemaCatalog,
            SchemaDocuments = schemaDocuments,
            Dimensions = new Dictionary<string, string>(stream.Dimensions, StringComparer.Ordinal)
        };
    }

    private static IEnumerable<FactSchemaDocument> PackageSchemas(
        LocalCollectorPackage package,
        CollectorOutputTemplate output) =>
        package.FactSchemas
            .Where(schema =>
                schema.SchemaId == output.Schema.Id &&
                schema.SchemaMajor == output.Schema.Major &&
                schema.FactKind == output.FactKind);

    private void ValidatePackageSchemaIdentities(LocalCollectorPackage package)
    {
        var existingStreams = _state.Streams.Concat(
            _pendingActivationCommits.Values.SelectMany(commit => commit.Streams));
        foreach (var schema in package.FactSchemas)
        {
            if (existingStreams.Any(stream =>
                    stream.SchemaId == schema.SchemaId &&
                    stream.SchemaMajor == schema.SchemaMajor &&
                    stream.SchemaCatalog.TryGetValue(schema.SchemaRevision, out var existingHash) &&
                    existingHash != schema.ContentHash))
                throw ActivationError(
                    "package_mismatch",
                    $"Fact Schema '{schema.SchemaId}/{schema.SchemaMajor}/{schema.SchemaRevision}' changed content hash across Package versions.");
        }
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
        stream.SchemaId == output.Schema.Id &&
        stream.SchemaMajor == output.Schema.Major &&
        stream.Dimensions.Count == dimensions.Count &&
        stream.Dimensions.All(pair => dimensions.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static FactStreamDescriptor ToDescriptor(FactStreamState stream) => new(
        stream.StreamId,
        stream.CollectorInstanceId,
        new SubjectReference(stream.SubjectId, stream.SubjectKind),
        stream.OutputId,
        stream.Source,
        stream.FactKind,
        new FactStreamSchemaReference(
            stream.SchemaId,
            stream.SchemaMajor,
            stream.SchemaRevision,
            stream.SchemaHash),
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
        var expectedFingerprint = KnownPackageFingerprint(
            package.Manifest.PackageId,
            package.Manifest.Version);
        if (expectedFingerprint is not null &&
            expectedFingerprint != package.PackageContentHash)
            throw ActivationError(
                "package_mismatch",
                "An immutable Collector Package version cannot resolve to a different content fingerprint.");
        if (!package.Manifest.Config.AcceptedVersions.Contains(instance.ConfigVersion))
            throw ActivationError(
                "config_version_unsupported",
                $"Collector Package '{package.Manifest.PackageId}/{package.Manifest.Version}' does not accept ConfigVersion {instance.ConfigVersion}.");
    }

    private string? KnownPackageFingerprint(string packageId, string packageVersion)
    {
        string? knownFingerprint = null;
        foreach (var instance in _state.Instances)
        {
            if (instance.PackageId != packageId ||
                !instance.PackageFingerprints.TryGetValue(packageVersion, out var fingerprint))
                continue;
            if (knownFingerprint is not null && knownFingerprint != fingerprint)
                throw new CollectorRuntimeStateException(
                    $"Collector Runtime state contains conflicting fingerprints for Package '{packageId}/{packageVersion}'.");
            knownFingerprint = fingerprint;
        }
        foreach (var pending in _pendingPackageFingerprints.Values)
        {
            if (pending.PackageId != packageId || pending.PackageVersion != packageVersion)
                continue;
            if (knownFingerprint is not null && knownFingerprint != pending.Fingerprint)
                throw new CollectorRuntimeStateException(
                    $"Collector Runtime state contains conflicting fingerprints for Package '{packageId}/{packageVersion}'.");
            knownFingerprint = pending.Fingerprint;
        }
        return knownFingerprint;
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
            if (capability == "facts.event" && _inputEventSink is null ||
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
        ActivationDeliveryCapability deliveryCapability) =>
        new(
            activationId,
            helloMessageId,
            package,
            new CollectorProtocolLimits(_options.MaxFactsPerBatch, _options.MaxBatchBytes),
            deliveryCapability,
            (streamId, facts) => CommitFacts(activationId, streamId, facts),
            (streamId, gap) => CommitGap(activationId, streamId, gap),
            MarkAcknowledgedLiveTraffic);

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

    private void RestorePersistedFactSchemas()
    {
        try
        {
            foreach (var stream in _state.Streams)
            {
                foreach (var pair in stream.SchemaDocuments)
                {
                    var contentHash = stream.SchemaCatalog[pair.Key];
                    if (_factSchemasByHash.TryGetValue(contentHash, out var cached))
                    {
                        if (cached.SchemaId != stream.SchemaId ||
                            cached.SchemaMajor != stream.SchemaMajor ||
                            cached.SchemaRevision != pair.Key ||
                            cached.FactKind != stream.FactKind)
                            throw new PackageValidationException(
                                $"Durable Fact Schema hash '{contentHash}' is bound to conflicting identities.");
                        continue;
                    }
                    var restored = LocalCollectorPackage.RestoreFactSchema(
                        pair.Value,
                        stream.SchemaId,
                        stream.SchemaMajor,
                        pair.Key,
                        stream.FactKind,
                        contentHash);
                    _factSchemasByHash.Add(contentHash, restored);
                }
            }
        }
        catch (PackageValidationException exception)
        {
            throw new CollectorRuntimeStateException(
                "Collector Runtime state contains an invalid durable Fact Schema snapshot.",
                exception);
        }
    }

    private void ReplayCommittedFacts()
    {
        lock (_gate)
        {
            foreach (var fact in _state.Facts)
            {
                var stream = _state.Streams.SingleOrDefault(candidate => candidate.StreamId == fact.StreamId);
                if (stream is not null)
                    ProjectFact(stream, fact, isReplay: true);
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
        var projector = ResolveSegmentProjector(stream.SchemaId, stream.SchemaMajor);
        if (projector is null)
        {
            Log.Error(
                "已持久接收 Collector Segment Fact {FactId}，但 schema {SchemaId}/{SchemaMajor} 无投影 adapter",
                fact.FactId,
                stream.SchemaId,
                stream.SchemaMajor);
            return;
        }
        if (fact.RecordState == FactRecordState.Retracted)
        {
            if (_segmentSink is ISubjectSegmentProjectionSink subjectSink)
            {
                try
                {
                    subjectSink.RetractDurable(
                        ContextForStream(stream),
                        projector.ProjectedId(stream.StreamId, fact.FactId),
                        fact.Revision);
                }
                catch (Exception exception)
                {
                    Log.Error(
                        exception,
                        "已持久接收 Collector Segment 撤回 {FactId}，从 Subject 缓冲移除失败；重启时将重放",
                        fact.FactId);
                }
            }
            else if (_segmentSink is IDurableSegmentProjectionSink durableSink)
            {
                try
                {
                    durableSink.RetractDurable(
                        projector.ProjectedId(stream.StreamId, fact.FactId),
                        fact.Revision);
                }
                catch (Exception exception)
                {
                    Log.Error(
                        exception,
                        "已持久接收 Collector Segment 撤回 {FactId}，从 Hub 缓冲移除失败；重启时将重放",
                        fact.FactId);
                }
            }
            return;
        }
        if (fact.Payload is not { } payload ||
            !projector.TryProject(
                stream,
                fact.FactId,
                fact.Start,
                fact.End,
                fact.IsFinal,
                payload,
                out var item))
        {
            Log.Error(
                "已持久接收 Collector Segment Fact {FactId}，但其 payload 无法由 schema adapter 投影",
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

    private bool ProjectEvent(FactStreamState stream, CommittedFactState fact, bool isReplay)
    {
        if (fact.RecordState != FactRecordState.Present ||
            fact.OccurredAt is not { } occurredAt ||
            fact.Payload is not { } payload ||
            ResolveEventProjector(stream.SchemaId, stream.SchemaMajor) is not { } projector ||
            !projector.TryProject(fact.FactId, occurredAt, payload, out var item))
        {
            Log.Error(
                "已持久接收 Collector Event Fact {FactId}，但其 payload 无法由 schema adapter 投影",
                fact.FactId);
            return false;
        }
        if (_inputEventSink is null)
        {
            Log.Error(
                "已持久接收 Collector Event Fact {FactId}，但未配置 InputEvent 投影 sink",
                fact.FactId);
            return false;
        }
        try
        {
            _inputEventSink.Accept(item!, isReplay);
            return true;
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

    private ISegmentFactProjector? ResolveSegmentProjector(string schemaId, int schemaMajor) =>
        _segmentProjectors.SingleOrDefault(projector => projector.Supports(schemaId, schemaMajor));

    private IEventFactProjector? ResolveEventProjector(string schemaId, int schemaMajor) =>
        _eventProjectors.SingleOrDefault(projector => projector.Supports(schemaId, schemaMajor));

    private sealed record OpenedBinding(string BindingId, FactStreamState Stream);
    private sealed record StreamOpenPlan(
        IReadOnlyList<OpenedBinding> Bindings,
        PendingActivationCommit Commit);
    private sealed record PendingActivationCommit(
        CollectorInstanceState Instance,
        IReadOnlyList<FactStreamState> Streams);
    private sealed record PendingPackageFingerprint(
        string PackageId,
        string PackageVersion,
        string Fingerprint);
    private sealed record HelloAttempt(
        string RequestHash,
        TaskCompletionSource<InProcessCollectorActivation> Completion);

    private sealed class StartingCollector(
        Guid collectorInstanceId,
        IInProcessCollector collector)
    {
        private readonly object _stopGate = new();
        private readonly TaskCompletionSource _activationCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _stopTask;
        private InProcessCollectorActivation? _activation;
        private bool _stopRequested;

        public Guid CollectorInstanceId { get; } = collectorInstanceId;
        public Task ActivationCompleted => _activationCompleted.Task;

        public void AttachActivation(InProcessCollectorActivation activation)
        {
            lock (_stopGate)
            {
                if (_stopRequested)
                    throw new ObjectDisposedException(nameof(InProcessCollectorActivation));
                _activation = activation;
            }
        }

        public async Task InvokeStreamsOpenedAsync(Func<ValueTask> callback)
        {
            ValueTask callbackTask;
            lock (_stopGate)
            {
                if (_stopRequested)
                    throw new ObjectDisposedException(nameof(InProcessCollectorActivation));
                callbackTask = callback();
            }
            await callbackTask;
        }

        public async Task StopAsync()
        {
            Task stopTask;
            lock (_stopGate)
            {
                _stopRequested = true;
                _stopTask ??= _activation is null
                    ? collector.StopAsync(CancellationToken.None).AsTask()
                    : _activation.StopAsync(CancellationToken.None).AsTask();
                stopTask = _stopTask;
            }
            try
            {
                await stopTask;
            }
            catch
            {
                lock (_stopGate)
                {
                    if (ReferenceEquals(_stopTask, stopTask))
                        _stopTask = null;
                }
                throw;
            }
        }

        public void MarkActivationCompleted() => _activationCompleted.TrySetResult();
    }
}
