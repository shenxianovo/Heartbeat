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
    ArtifactHashMismatch
}
