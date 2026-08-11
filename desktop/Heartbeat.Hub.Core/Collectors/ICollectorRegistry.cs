namespace Heartbeat.Hub.Core.Collectors;

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
}
