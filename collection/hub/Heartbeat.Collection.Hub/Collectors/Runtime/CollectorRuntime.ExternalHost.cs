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
            if (_startingInstances.Contains(collectorInstanceId) ||
                _activations.Values.Any(activation =>
                    activation.State != CollectorActivationState.Stopped &&
                    activation.Streams.Values.Any(stream =>
                        stream.Descriptor.CollectorInstanceId == collectorInstanceId)) ||
                _externalHostActivations.Values.Any(activation =>
                    activation.State != CollectorActivationState.Stopped &&
                    activation.Streams.Values.Any(stream =>
                        stream.CollectorInstanceId == collectorInstanceId)))
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
            _pendingExternalHostActivations.Add(
                activationId,
                new PendingExternalHostActivation(
                    helloMessageId,
                    instance,
                    package));

            return new ExternalHostCollectorInitialization(
                activationId,
                instance,
                instance.Spec,
                new CollectorProtocolLimits(
                    _options.MaxFactsPerBatch,
                    _options.MaxBatchBytes,
                    _options.MaxInFlightBatches),
                SelectedCapabilities(package, support!));
        }
    }

    public ExternalHostCollectorActivation ReadyExternalHostActivation(
        Guid activationId,
        long appliedSpecRevision,
        IReadOnlyList<OutputBinding> bindings)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingExternalHostActivations.TryGetValue(activationId, out var pending))
                throw ActivationError("protocol_invalid_message", "ExternalHost Activation is not awaiting ready.");
            if (appliedSpecRevision != pending.Instance.Spec.SpecRevision)
                throw ActivationError("spec_revision_stale", "Collector did not apply the current SpecRevision.");

            var streamPlan = PlanStreams(activationId, pending.Instance, pending.Package, bindings);
            var descriptors = streamPlan.Bindings.ToImmutableDictionary(
                pair => pair.BindingId,
                pair => ToDescriptor(pair.Stream),
                StringComparer.Ordinal);
            var activation = new ExternalHostCollectorActivation(
                this,
                activationId,
                pending.HelloMessageId,
                pending.Package,
                descriptors);

            var next = _state.WithInstanceAndStreams(
                streamPlan.Commit.Instance,
                streamPlan.Commit.Streams);
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
            foreach (var schema in pending.Package.FactSchemas)
                _factSchemasByHash[schema.ContentHash] = schema;
            foreach (var stream in descriptors.Values)
                _streamWriters[stream.StreamId] = activationId;
            _externalHostActivations.Add(activationId, activation);
            _pendingExternalHostActivations.Remove(activationId);
            _pendingPackageFingerprints.Remove(activationId);
            _startingInstances.Remove(pending.Instance.CollectorInstanceId);
            return activation;
        }
    }

    public void AbandonExternalHostActivation(Guid activationId)
    {
        lock (_gate)
        {
            if (!_pendingExternalHostActivations.Remove(activationId, out var pending))
                return;
            _pendingPackageFingerprints.Remove(activationId);
            _startingInstances.Remove(pending.Instance.CollectorInstanceId);
        }
    }

    public void StopExternalHostActivation(
        ExternalHostCollectorActivation activation,
        ExternalHostActivationStopReason reason)
    {
        ArgumentNullException.ThrowIfNull(activation);
        lock (_gate)
        {
            if (activation.State == CollectorActivationState.Stopped)
                return;
            activation.State = CollectorActivationState.Draining;
            foreach (var streamId in activation.Streams.Values.Select(stream => stream.StreamId))
            {
                if (_streamWriters.TryGetValue(streamId, out var writer) && writer == activation.ActivationId)
                    _streamWriters.Remove(streamId);
            }
            activation.StopReason = reason;
            activation.State = CollectorActivationState.Stopped;
            _externalHostActivations.Remove(activation.ActivationId);
        }
        ForgetActivationAttempts(activation.ActivationId);
    }

    private void ForgetActivationAttempts(Guid activationId)
    {
        lock (_publishAttemptGate)
        {
            foreach (var key in _publishAttempts.Keys.Where(key => key.ActivationId == activationId).ToArray())
                _publishAttempts.Remove(key);
        }
        lock (_messageAttemptGate)
        {
            foreach (var key in _messageAttempts.Keys.Where(key => key.ActivationId == activationId).ToArray())
                _messageAttempts.Remove(key);
        }
        lock (_gate)
        {
            foreach (var key in _gapReplays.Keys.Where(key => key.ActivationId == activationId).ToArray())
                _gapReplays.Remove(key);
        }
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
        Guid HelloMessageId,
        CollectorInstance Instance,
        LocalCollectorPackage Package);
}
