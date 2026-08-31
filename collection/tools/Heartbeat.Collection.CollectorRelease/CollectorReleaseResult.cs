using Heartbeat.Collection.Hub.Collectors.Delivery;

namespace Heartbeat.Collection.CollectorRelease;

/// <summary>Why a release was refused. Every value is a fail-closed condition.</summary>
public enum CollectorReleaseFailure
{
    /// <summary>The tag is not <c>collector-{slug}/v{semver}</c>.</summary>
    InvalidTag,

    /// <summary>No Collector publishes under that tag slug.</summary>
    UnknownReleaseTarget,

    /// <summary>The staged directory is not a loadable Collector Package.</summary>
    PackageLoadFailed,

    /// <summary>The Package manifest names a different PackageId than the release target.</summary>
    PackageIdMismatch,

    /// <summary>The tag version and the Package manifest version disagree.</summary>
    VersionMismatch,

    /// <summary>The artifact is not framework-dependent.</summary>
    SelfContainedArtifact,

    /// <summary>This Version is already published with different content; publish a new version.</summary>
    VersionAlreadyPublished,

    /// <summary>The index the Runtime would read does not describe what was staged.</summary>
    IndexVerificationFailed
}

/// <summary>The outcome of staging one release, including the evidence lines the operator sees.</summary>
public sealed record CollectorReleaseResult(
    bool Succeeded,
    CollectorReleaseFailure? Failure,
    CollectorRegistryFailureReason? RegistryReason,
    string Detail,
    CollectorRegistryIndex? Index,
    string? IndexPath,
    string? ArtifactPath,
    string? PackageContentHash,
    IReadOnlyList<string> Report)
{
    public static CollectorReleaseResult Refuse(
        CollectorReleaseFailure failure,
        string detail,
        CollectorRegistryFailureReason? registryReason = null) =>
        new(false, failure, registryReason, detail, null, null, null, null, []);
}
