using Heartbeat.Collection.Hub.Collectors.Delivery;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

/// <summary>
/// The Collector Instance's own record of Collector Package delivery: what the Registry last
/// advertised, what is installed, what the owner approved, and why the last manual check failed.
///
/// It deliberately lives on <see cref="CollectorRuntime" /> rather than in a store of its own.
/// Last-Known-Good already lives here, and approval is the other half of the same lifecycle: a
/// separate approval file would be a second version-state authority that could drift from the one
/// the Runtime uses when it decides what to run. Everything here writes through the same durable
/// Collector Runtime State, under the same lock, so <c>Current</c> can only ever be a projection.
///
/// Nothing in this file starts, stops or switches an Activation, and nothing here rewrites Collector
/// Desired State. Approving a candidate is not Ready.
/// </summary>
public sealed partial class CollectorRuntime
{
    /// <summary>Projects the persisted update state of one Collector Instance.</summary>
    public CollectorPackageUpdateStatus GetPackageUpdateStatus(Guid collectorInstanceId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return ProjectPackageUpdate(RequireInstanceStateLocked(collectorInstanceId));
        }
    }

    /// <summary>
    /// Records the outcome of exactly one manual check. A <c>null</c>
    /// <paramref name="registryCurrent" /> means the Registry index was not read this time and the
    /// previously observed candidate stays; a <c>null</c> <paramref name="installedCandidate" />
    /// means nothing new was installed, never that an existing Collector Installation went away.
    ///
    /// <paramref name="failure" /> is the only way the last error changes: passing <c>null</c>
    /// clears it because that check succeeded end to end, and passing a failure replaces it.
    /// </summary>
    public CollectorPackageUpdateStatus RecordPackageUpdateCheck(
        Guid collectorInstanceId,
        CollectorPackageReference? registryCurrent,
        DateTimeOffset? registryCheckedAt,
        CollectorPackageReference? installedCandidate,
        CollectorPackageUpdateFailure? failure)
    {
        if (registryCurrent is not null && registryCheckedAt is null)
            throw new ArgumentNullException(
                nameof(registryCheckedAt),
                "A Registry candidate must be recorded with the time it was read.");

        lock (_gate)
        {
            ThrowIfDisposed();
            var instance = RequireInstanceStateLocked(collectorInstanceId);
            var current = instance.PackageUpdate ?? new CollectorPackageUpdateStateRecord();
            return PersistPackageUpdateLocked(instance, current with
            {
                RegistryCurrent = registryCurrent is null
                    ? current.RegistryCurrent
                    : ToRecord(instance, registryCurrent),
                RegistryCheckedAt = registryCurrent is null
                    ? current.RegistryCheckedAt
                    : registryCheckedAt!.Value.ToUniversalTime(),
                InstalledCandidate = installedCandidate is null
                    ? current.InstalledCandidate
                    : ToRecord(instance, installedCandidate),
                LastFailure = failure is null
                    ? null
                    : new CollectorPackageUpdateFailureRecord
                    {
                        Reason = failure.Reason,
                        Message = failure.Message,
                        OccurredAt = failure.OccurredAt.ToUniversalTime()
                    }
            });
        }
    }

    /// <summary>
    /// Records the owner's approval of one exact Collector Package reference. Whether that reference
    /// really is a Collector Installation is decided before this call by the installation store's
    /// admission function; this only persists the decision, and it neither promotes the candidate
    /// nor demotes the current Last-Known-Good.
    /// </summary>
    public CollectorPackageUpdateStatus ApprovePackageCandidate(
        Guid collectorInstanceId,
        CollectorPackageReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_gate)
        {
            ThrowIfDisposed();
            var instance = RequireInstanceStateLocked(collectorInstanceId);
            var current = instance.PackageUpdate ?? new CollectorPackageUpdateStateRecord();
            return PersistPackageUpdateLocked(
                instance,
                current with { ApprovedCandidate = ToRecord(instance, reference) });
        }
    }

    private CollectorPackageUpdateStatus PersistPackageUpdateLocked(
        CollectorInstanceState instance,
        CollectorPackageUpdateStateRecord update)
    {
        var updated = instance with { PackageUpdate = update };
        var next = _state.WithInstanceAndStreams(updated, []);
        _store.Save(next);
        _state = next;
        return ProjectPackageUpdate(updated);
    }

    private CollectorInstanceState RequireInstanceStateLocked(Guid collectorInstanceId) =>
        _state.Instances.SingleOrDefault(instance => instance.CollectorInstanceId == collectorInstanceId)
        ?? throw new KeyNotFoundException($"Collector Instance '{collectorInstanceId}' was not found.");

    private static CollectorPackageReferenceRecord ToRecord(
        CollectorInstanceState instance,
        CollectorPackageReference reference)
    {
        if (!reference.IsWellFormed)
            throw new ArgumentException(
                $"Collector Package candidate '{reference}' is not well formed.",
                nameof(reference));
        if (!string.Equals(reference.PackageId, instance.PackageId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Collector Instance '{instance.CollectorInstanceId}' runs " +
                $"'{instance.PackageId}', not '{reference.PackageId}'.",
                nameof(reference));
        return new CollectorPackageReferenceRecord
        {
            PackageId = reference.PackageId,
            Version = reference.Version,
            ArtifactSha256 = reference.ArtifactSha256
        };
    }

    private static CollectorPackageUpdateStatus ProjectPackageUpdate(CollectorInstanceState instance)
    {
        var update = instance.PackageUpdate;
        return new CollectorPackageUpdateStatus(
            instance.CollectorInstanceId,
            instance.PackageId,
            instance.PackageVersion,
            instance.PackageContentHash,
            instance.LastKnownGoodPackage,
            ToReference(update?.InstalledCandidate),
            ToReference(update?.ApprovedCandidate),
            ToReference(update?.RegistryCurrent),
            update?.RegistryCheckedAt,
            update?.LastFailure is { } failure
                ? new CollectorPackageUpdateFailure(failure.Reason, failure.Message, failure.OccurredAt)
                : null);
    }

    private static CollectorPackageReference? ToReference(CollectorPackageReferenceRecord? record) =>
        record is null
            ? null
            : new CollectorPackageReference(record.PackageId, record.Version, record.ArtifactSha256);
}
