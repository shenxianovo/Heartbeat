namespace Heartbeat.Collection.Hub.Collectors;

public sealed record CollectorRegistration(
    bool Enabled,
    int? FlushPeriodMs,
    string? DeclarationJson,
    int? DeclarationVersion);

/// <summary>hub 持久化 Collector Registry 的 seam。</summary>
public interface ICollectorRegistry
{
    IReadOnlyDictionary<string, CollectorRegistration> Snapshot { get; }
    CollectorRegistration Touch(string source, int? flushPeriodMs = null);
    void Discover(IEnumerable<string> sources);
    void StoreDeclaration(string source, string declarationJson, int version);

    /// <summary>
    /// Stores a declaration verified from the active Collector Package. Package content is
    /// authoritative over a legacy self-reported document at the same semantic version.
    /// </summary>
    void StoreVerifiedPackageDeclaration(string source, string declarationJson, int version) =>
        StoreDeclaration(source, declarationJson, version);
}
