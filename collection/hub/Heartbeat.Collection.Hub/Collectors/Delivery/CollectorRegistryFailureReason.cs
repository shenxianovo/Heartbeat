namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// The closed set of reasons a Registry index read or artifact download can fail. Callers branch on
/// these values; the accompanying detail string is diagnostic text and is never a matching surface.
/// </summary>
public enum CollectorRegistryFailureReason
{
    /// <summary>The configured Registry base URI is not absolute, not a directory, or not HTTPS outside loopback.</summary>
    InvalidRegistryBaseUri,

    /// <summary>The requested PackageId is not a well-formed Collector PackageId.</summary>
    InvalidPackageId,

    /// <summary>The transport failed, or the Registry answered with a non-success status.</summary>
    RequestFailed,

    /// <summary>The document is not strict JSON.</summary>
    MalformedJson,

    /// <summary>The document repeats a property name; the intended value would be ambiguous.</summary>
    DuplicateJsonProperty,

    /// <summary>A required property is absent.</summary>
    MissingField,

    /// <summary>The document carries a property this schema version does not define.</summary>
    UnknownField,

    /// <summary>The document declares a schema version this reader does not implement.</summary>
    UnsupportedSchemaVersion,

    /// <summary>The document describes a different Package than the one requested.</summary>
    PackageIdMismatch,

    /// <summary>The declared Version is not a valid Collector Package SemVer.</summary>
    InvalidVersion,

    /// <summary>The artifact URL is missing, not absolute, or not in canonical form.</summary>
    InvalidArtifactUrl,

    /// <summary>The artifact URL leaves the Registry origin or this Package version directory.</summary>
    ArtifactUrlOutsideRegistry,

    /// <summary>The declared artifact length is not a positive integer.</summary>
    InvalidArtifactLength,

    /// <summary>The declared artifact SHA-256 is not 64 lowercase hex characters.</summary>
    InvalidArtifactSha256,

    /// <summary>A redirect pointed outside the Registry origin or the permitted directory.</summary>
    RedirectOutsideRegistry,

    /// <summary>The Registry redirected more times than this reader follows.</summary>
    TooManyRedirects,

    /// <summary>The downloaded byte count differs from the declared length.</summary>
    ArtifactLengthMismatch,

    /// <summary>The downloaded bytes hash to something other than the declared SHA-256.</summary>
    ArtifactHashMismatch,

    /// <summary>The caller cancelled the read, download or installation.</summary>
    Cancelled,

    /// <summary>The downloaded artifact is not a readable zip archive.</summary>
    MalformedArchive,

    /// <summary>
    /// An archive entry is not a plain relative file inside the destination: traversal, a rooted or
    /// drive-qualified path, a separator variant, a symbolic link or another non-regular entry, or a
    /// name that would collide with an entry already extracted.
    /// </summary>
    UnsafeArchiveEntry,

    /// <summary>The archive declares more entries or more uncompressed bytes than the limits allow.</summary>
    ArchiveLimitExceeded,

    /// <summary>The unpacked content is not a valid Collector Package according to the Package loader.</summary>
    PackageValidationFailed,

    /// <summary>The Collector Package manifest declares a different PackageId or Version than the candidate.</summary>
    PackageManifestMismatch,

    /// <summary>The directory that would own this candidate carries no completion marker, so it is not an Installation.</summary>
    InstallationMarkerMissing,

    /// <summary>
    /// The completion marker is unreadable, or it names another PackageId, Version, artifact
    /// SHA-256 or Package content hash than the candidate being asked about.
    /// </summary>
    InstallationMarkerMismatch,

    /// <summary>Local storage refused the installation: an I/O error, no space, or a permission error.</summary>
    InstallationStorageFailed,

    /// <summary>
    /// This Hub has no Official Collector Package Registry configured, so a manual check has nowhere
    /// to read from. It is a deployment gap, not a Registry failure.
    /// </summary>
    RegistryNotConfigured,

    /// <summary>
    /// The exact candidate names a Collector Package other than the one this Collector Instance is
    /// permanently bound to, so approving it would approve something this Instance can never run.
    /// </summary>
    CollectorInstancePackageMismatch
}
