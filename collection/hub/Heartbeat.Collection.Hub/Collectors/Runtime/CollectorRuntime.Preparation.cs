using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public sealed partial class CollectorRuntime
{
    private bool _preparationClosed;

    /// <summary>
    /// Selects a verified local ExternalHost Package before this Runtime has started any Activation.
    /// The caller owns the Profile lock and installs the immutable candidate first. This is not a live update.
    /// Existing identity, config, streams, secrets and durable delivery remain owned by this Runtime.
    /// </summary>
    public CollectorInstance PrepareExternalHostPackage(LocalCollectorPackage package, SubjectReference subject)
    {
        ArgumentNullException.ThrowIfNull(package);
        var blueprint = package.Manifest.DefaultInstance
            ?? throw new PackageValidationException("Development Package requires a default Instance blueprint.");
        if (!string.Equals(blueprint.SubjectKind, subject.Kind.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new PackageValidationException("Development Package Subject does not match this Host.");
        _ = ResolveProtocolArtifact(package, "externalHost");
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_preparationClosed)
                throw new InvalidOperationException("Local Package preparation requires a freshly opened, stopped Runtime.");
            var existing = FindInstance(package.Manifest.PackageId, subject, DefaultInstanceKey);
            if (existing is null)
                return CreateInstance(package, subject,
                    new CollectorInstanceSpec(1, blueprint.ConfigVersion, blueprint.Config.Clone()), DefaultInstanceKey);
            var current = GetInstanceStateLocked(existing.CollectorInstanceId);
            ValidatePackageCandidate(current, package);
            var updated = current with { PackageVersion = package.Manifest.Version, PackageContentHash = package.PackageContentHash };
            var next = _state.WithInstanceAndStreams(updated, []);
            _store.Save(next);
            _state = next;
            return ToPublic(updated);
        }
    }
}
