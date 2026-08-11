using Heartbeat.Hub.Core.Ingest;

namespace Heartbeat.Agent.Collectors;

/// <summary>Windows 平台的逻辑 AppHint → 可观测进程身份映射。</summary>
public sealed class WindowsCollectorAppHintResolver : ICollectorAppHintResolver
{
    public CollectorAppHintResolution Resolve(string appHint) => appHint switch
    {
        "chrome" => CollectorAppHintResolution.Resolved("win:chrome"),
        "edge" => CollectorAppHintResolution.Resolved("win:msedge"),
        "brave" => CollectorAppHintResolution.Resolved("win:brave"),
        "opera" => CollectorAppHintResolution.Resolved("win:opera"),
        "vivaldi" => CollectorAppHintResolution.Resolved("win:vivaldi"),
        "firefox" => CollectorAppHintResolution.Resolved("win:firefox"),
        _ => CollectorAppHintResolution.Unknown
    };
}
