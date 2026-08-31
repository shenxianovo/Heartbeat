using System.Text.RegularExpressions;
using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// One exact Collector Package candidate: the PackageId, the declared Version and the SHA-256 of the
/// artifact bytes the Registry bound to it. There is no inexact form of this type — no channel, no
/// range, no "latest" — because everything downstream (the installation directory, the completion
/// marker, and later the owner's approval) has to name the same single candidate.
///
/// <see cref="ArtifactSha256" /> is the raw artifact hash from the Registry index, not the Package
/// manifest's prefixed content hash. Both are recorded by the completion marker; the manifest stays
/// the authority for Package identity.
/// </summary>
public sealed record CollectorPackageReference(string PackageId, string Version, string ArtifactSha256)
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public static CollectorPackageReference FromIndex(CollectorRegistryIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return new CollectorPackageReference(index.PackageId, index.Version, index.Artifact.Sha256);
    }

    /// <summary>
    /// Rejects a candidate whose parts are not well formed, reusing the Registry index reasons. This
    /// runs before any path is built from it, so a hostile PackageId or Version can never become a
    /// directory name.
    /// </summary>
    public CollectorRegistryResult<CollectorPackageReference> Validated()
    {
        if (string.IsNullOrEmpty(PackageId) || !LocalCollectorPackage.IsValidPackageId(PackageId))
            return CollectorRegistryResult<CollectorPackageReference>.Failure(
                CollectorRegistryFailureReason.InvalidPackageId,
                $"PackageId '{PackageId}' is not a well-formed Collector PackageId.");
        if (string.IsNullOrEmpty(Version) || !LocalCollectorPackage.IsValidSemVer(Version))
            return CollectorRegistryResult<CollectorPackageReference>.Failure(
                CollectorRegistryFailureReason.InvalidVersion,
                $"Version '{Version}' is not a valid Collector Package SemVer.");
        if (!Sha256Pattern.IsMatch(ArtifactSha256))
            return CollectorRegistryResult<CollectorPackageReference>.Failure(
                CollectorRegistryFailureReason.InvalidArtifactSha256,
                $"Artifact SHA-256 '{ArtifactSha256}' must be 64 lowercase hex characters.");
        return CollectorRegistryResult<CollectorPackageReference>.Success(this);
    }

    public bool IsWellFormed => Validated().IsSuccess;

    public override string ToString() => $"{PackageId}@{Version} ({ArtifactSha256})";
}
