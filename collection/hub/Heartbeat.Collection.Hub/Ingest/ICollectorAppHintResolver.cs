using Heartbeat.Core;

namespace Heartbeat.Collection.Hub.Ingest;

/// <summary>
/// 平台 adapter 对外部 Collector 的逻辑 AppHint 做本机 AppIdentity 解析。
/// Collection Hub 只承载解析端口与结果语义，不包含进程名或 bundle identifier 知识。
/// </summary>
public interface ICollectorAppHintResolver
{
    CollectorAppHintResolution Resolve(string appHint);
}

public enum CollectorAppHintResolutionKind
{
    Resolved,
    Unknown,
    Ambiguous
}

public readonly record struct CollectorAppHintResolution(
    CollectorAppHintResolutionKind Kind,
    string? AppIdentityKey)
{
    public static CollectorAppHintResolution Resolved(string appIdentityKey) =>
        new(CollectorAppHintResolutionKind.Resolved, AppIdentityKeys.Normalize(appIdentityKey));

    public static CollectorAppHintResolution Unknown =>
        new(CollectorAppHintResolutionKind.Unknown, null);

    public static CollectorAppHintResolution Ambiguous =>
        new(CollectorAppHintResolutionKind.Ambiguous, null);
}

/// <summary>无平台 head 的 hub 不猜测 App 关联；外部段仍会被保留。</summary>
internal sealed class NullCollectorAppHintResolver : ICollectorAppHintResolver
{
    public CollectorAppHintResolution Resolve(string appHint) => CollectorAppHintResolution.Unknown;
}
