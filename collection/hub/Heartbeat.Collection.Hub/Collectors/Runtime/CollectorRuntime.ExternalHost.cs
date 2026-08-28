using System.Collections.Immutable;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public sealed partial class CollectorRuntime
{
    private readonly Dictionary<Guid, PendingExternalHostActivation> _pendingExternalHostActivations = [];
    private readonly Dictionary<Guid, ExternalHostCollectorActivation> _externalHostActivations = [];

    public ExternalHostCollectorInitialization BeginExternalHostActivation(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        string artifactId,
        string artifactHash,
        ProtocolSupport protocolSupport,
        Guid activationId,
        Guid helloMessageId)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(protocolSupport);
        if (!IsUuidV7(activationId) || !IsUuidV7(helloMessageId))
            throw ActivationError(
                "protocol_invalid_message",
                "ExternalHost activationId and activation.hello messageId must be UUIDv7 values.");

        var support = SnapshotProtocolSupport(protocolSupport);
        var helloRequestHash = HelloRequestHash(
            collectorInstanceId,
            package,
            artifactId,
            support);
        lock (_gate)
        {
            ThrowIfDisposed();
            var instanceState = GetInstanceStateLocked(collectorInstanceId);
            ValidatePackageCandidate(instanceState, package);
            var artifact = ResolveExternalHostArtifact(package, artifactId);
            if (!string.Equals(artifact.ContentHash, artifactHash, StringComparison.Ordinal))
                throw ActivationError("package_mismatch", "ExternalHost Artifact hash does not match the verified Package.");
            ValidateProtocolSupport(package, support);
            if (_pendingExternalHostActivations.ContainsKey(activationId) ||
                _externalHostActivations.ContainsKey(activationId))
                throw ActivationError("protocol_invalid_message", "ExternalHost activationId is already in use.");
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

            var instance = ToPublic(instanceState) with
            {
                PackageVersion = package.Manifest.Version,
                PackageContentHash = package.PackageContentHash
            };
            _startingInstances.Add(collectorInstanceId);
            _pendingPackageFingerprints.Add(
                activationId,
                new PendingPackageFingerprint(
                    package.Manifest.PackageId,
                    package.Manifest.Version,
                    package.PackageContentHash));
            var session = CreateActivationSession(
                activationId,
                helloMessageId,
                package,
                ActivationDeliveryCapability.Complete);
            _pendingExternalHostActivations.Add(
                activationId,
                new PendingExternalHostActivation(instance, package, session));

            return new ExternalHostCollectorInitialization(
                activationId,
                instance,
                instance.Spec,
                new CollectorProtocolLimits(
                    _options.MaxFactsPerBatch,
                    _options.MaxBatchBytes),
                new CollectorResources(null),
                SelectedCapabilities(package, support!)
                    .Where(capability => capability.Key != "resources.instance-data")
                    .ToImmutableDictionary(
                        capability => capability.Key,
                        capability => capability.Value,
                        StringComparer.Ordinal));
        }
    }

    public ExternalHostCollectorActivation ReadyExternalHostActivation(
        Guid activationId,
        long appliedSpecRevision,
        IReadOnlyList<OutputBinding> bindings)
    {
        var activation = OpenExternalHostStreams(activationId, appliedSpecRevision, bindings);
        return MarkExternalHostReady(activation, appliedSpecRevision);
    }

    public ExternalHostCollectorActivation OpenExternalHostStreams(
        Guid activationId,
        long appliedSpecRevision,
        IReadOnlyList<OutputBinding> bindings)
    {
        PendingExternalHostActivation pending;
        StreamOpenPlan streamPlan;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingExternalHostActivations.TryGetValue(activationId, out var candidate))
                throw ActivationError("protocol_invalid_message", "ExternalHost Activation is not awaiting ready.");
            pending = candidate;
            if (appliedSpecRevision != pending.Instance.Spec.SpecRevision)
                throw ActivationError("spec_revision_stale", "Collector did not apply the current SpecRevision.");
            streamPlan = PlanStreams(activationId, pending.Instance, pending.Package, bindings);
        }

        pending.Session.AcceptInitialized(appliedSpecRevision, pending.Instance.Spec.SpecRevision);
        var descriptors = streamPlan.Bindings.ToImmutableDictionary(
            pair => pair.BindingId,
            pair => ToDescriptor(pair.Stream),
            StringComparer.Ordinal);
        pending.Session.AcceptStreams(descriptors);
        var activation = new ExternalHostCollectorActivation(pending.Session);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingExternalHostActivations.TryGetValue(activationId, out var current) ||
                !ReferenceEquals(current, pending))
                throw ActivationError(
                    "activation_stopping",
                    "ExternalHost Activation ended while its Streams were opening.");
            _externalHostActivations.Add(activationId, activation);
            _pendingActivationCommits.Add(activationId, streamPlan.Commit);
            _pendingExternalHostActivations.Remove(activationId);
            return activation;
        }
    }

    public ExternalHostCollectorActivation MarkExternalHostReady(
        ExternalHostCollectorActivation activation,
        long appliedSpecRevision)
    {
        ArgumentNullException.ThrowIfNull(activation);
        long expectedSpecRevision;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (activation.State == CollectorActivationState.Ready)
                return activation;
            if (!_pendingActivationCommits.TryGetValue(activation.ActivationId, out var pendingCommit))
                throw ActivationError("protocol_invalid_message", "ExternalHost Activation has no pending Stream commit.");
            expectedSpecRevision = pendingCommit.Instance.SpecRevision;
        }
        activation.Session.AcceptReady(
            appliedSpecRevision,
            expectedSpecRevision,
            () => CommitExternalHostReady(activation));
        return activation;
    }

    private void CommitExternalHostReady(ExternalHostCollectorActivation activation)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingActivationCommits.TryGetValue(activation.ActivationId, out var pendingCommit))
                throw ActivationError("protocol_invalid_message", "ExternalHost Activation has no pending Stream commit.");
            var next = _state.WithInstanceAndStreams(pendingCommit.Instance, pendingCommit.Streams);
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
            foreach (var schema in activation.Package.FactSchemas)
                _factSchemasByHash[schema.ContentHash] = schema;
            foreach (var stream in activation.Streams.Values)
                _streamWriters[stream.StreamId] = activation.ActivationId;
            _pendingActivationCommits.Remove(activation.ActivationId);
            _pendingPackageFingerprints.Remove(activation.ActivationId);
            _startingInstances.Remove(pendingCommit.Instance.CollectorInstanceId);
        }
    }

    public void AbandonExternalHostActivation(Guid activationId)
    {
        PendingExternalHostActivation? pending;
        lock (_gate)
        {
            if (!_pendingExternalHostActivations.Remove(activationId, out pending))
                return;
        }
        pending.Session.CompleteStop(() =>
        {
            lock (_gate)
            {
                _pendingPackageFingerprints.Remove(activationId);
                _startingInstances.Remove(pending.Instance.CollectorInstanceId);
            }
        });
    }

    public void StopExternalHostActivation(
        ExternalHostCollectorActivation activation,
        ExternalHostActivationStopReason reason)
    {
        ArgumentNullException.ThrowIfNull(activation);
        activation.Session.CompleteStop(() =>
        {
            lock (_gate)
            {
                foreach (var streamId in activation.Streams.Values.Select(stream => stream.StreamId))
                {
                    if (_streamWriters.TryGetValue(streamId, out var writer) && writer == activation.ActivationId)
                        _streamWriters.Remove(streamId);
                }
                _externalHostActivations.Remove(activation.ActivationId);
                if (_pendingActivationCommits.Remove(activation.ActivationId, out var pendingCommit))
                    _startingInstances.Remove(pendingCommit.Instance.CollectorInstanceId);
                _pendingPackageFingerprints.Remove(activation.ActivationId);
            }
        }, reason);
    }

    private static VerifiedCollectorArtifact ResolveExternalHostArtifact(
        LocalCollectorPackage package,
        string artifactId)
    {
        var operatingSystem = CurrentOperatingSystem();
        var architecture = CurrentArchitecture();
        var candidates = package.Manifest.Artifacts.Where(artifact =>
            artifact.Driver == "externalHost" &&
            artifact.OperatingSystems.Contains(operatingSystem, StringComparer.Ordinal) &&
            artifact.Architectures.Contains(architecture, StringComparer.Ordinal)).ToArray();
        if (candidates.Length != 1)
            throw ActivationError(
                "package_mismatch",
                $"Collector Package must have exactly one Artifact for externalHost/{operatingSystem}/{architecture}; found {candidates.Length}.");
        if (candidates[0].ArtifactId != artifactId)
            throw ActivationError("package_mismatch", $"Artifact '{artifactId}' is not the selected current ExternalHost target.");
        return package.Artifacts.Single(artifact => artifact.ArtifactId == candidates[0].ArtifactId);
    }

    private static IReadOnlyDictionary<string, int> SelectedCapabilities(
        LocalCollectorPackage package,
        ProtocolSupport support)
    {
        var selected = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var capability in support.Capabilities)
        {
            if (!HubProtocolCapabilities.TryGetValue(capability.Key, out var hub) ||
                !package.Manifest.SupportedCapabilities.TryGetValue(capability.Key, out var declared))
                continue;
            var version = hub.Intersect(declared).Intersect(capability.Value).DefaultIfEmpty().Max();
            if (version > 0)
                selected[capability.Key] = version;
        }
        return selected.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private sealed record PendingExternalHostActivation(
        CollectorInstance Instance,
        LocalCollectorPackage Package,
        CollectorActivationSession Session);
}
