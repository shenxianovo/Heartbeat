namespace Heartbeat.Collection.Hub.Collectors;

public sealed record CollectorRegistration(
    bool Enabled,
    int? FlushPeriodMs,
    string? DeclarationJson,
    int? DeclarationVersion);

public interface ICollectorDeclarationStore
{
    IReadOnlyDictionary<string, CollectorRegistration> Snapshot { get; }
    void StoreVerifiedPackageDeclaration(string source, string declarationJson, int version);
}

/// <summary>Legacy source-level registry. New Runtime code must depend on a narrower seam.</summary>
public interface ICollectorRegistry : ICollectorDeclarationStore
{
    CollectorRegistration Touch(string source, int? flushPeriodMs = null);
    void Discover(IEnumerable<string> sources);
    void StoreDeclaration(string source, string declarationJson, int version);

    /// <summary>
    /// Stores a declaration verified from the active Collector Package. Package content is
    /// authoritative over a legacy self-reported document at the same semantic version.
    /// </summary>
    void ICollectorDeclarationStore.StoreVerifiedPackageDeclaration(
        string source,
        string declarationJson,
        int version) =>
        StoreDeclaration(source, declarationJson, version);
}
