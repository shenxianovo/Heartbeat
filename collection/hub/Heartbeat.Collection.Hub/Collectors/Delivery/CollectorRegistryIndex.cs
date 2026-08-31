namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// The minimal per-Package Official Collector Package Registry index (ADR-047). It names exactly one
/// candidate: the PackageId, the Version, and the artifact's URL, byte length and SHA-256.
///
/// It deliberately does not restate Package identity. The Collector Package manifest read by
/// <see cref="Packages.LocalCollectorPackage" /> stays the only authority for what a Package
/// <em>is</em>; this index only says where its bytes live and which bytes to accept. It carries no
/// channel, signature, compatibility matrix, release note or timestamp: those are either deferred
/// production scope (ADR-045) or belong to the Package loader and the Collector Protocol handshake.
/// </summary>
public sealed record CollectorRegistryIndex(
    int SchemaVersion,
    string PackageId,
    string Version,
    CollectorRegistryArtifact Artifact);

/// <summary>
/// The exact artifact bytes a Registry index points at. <see cref="Sha256" /> is 64 lowercase hex
/// characters over the raw artifact bytes; it is not the Package manifest's prefixed content hash.
/// </summary>
public sealed record CollectorRegistryArtifact(Uri Url, long Length, string Sha256);
