using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// A Collector Installation: the local fact that this machine fully holds one exact Collector
/// Package. It can only be produced by
/// <see cref="CollectorInstallationStore.OpenInstallation" />, which is the single place that
/// decides whether a directory qualifies, so a half-downloaded or half-extracted directory can
/// never be handed to a caller as one.
///
/// Holding an Installation says nothing about enablement, approval or activation.
/// </summary>
public sealed class CollectorInstallation
{
    internal CollectorInstallation(CollectorPackageReference reference, LocalCollectorPackage package)
    {
        Reference = reference;
        Package = package;
    }

    /// <summary>The exact candidate this Installation holds.</summary>
    public CollectorPackageReference Reference { get; }

    /// <summary>The verified Package snapshot, loaded from <see cref="Directory" />.</summary>
    public LocalCollectorPackage Package { get; }

    /// <summary>The version- and content-isolated directory that owns this Installation.</summary>
    public string Directory => Package.PackageDirectory;

    /// <summary>The Package manifest content hash, as computed by the Package loader.</summary>
    public string PackageContentHash => Package.PackageContentHash;
}
