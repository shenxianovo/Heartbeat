using Heartbeat.Collection.Hub.Ingest;

namespace Heartbeat.Desktop.Mac.Collectors;

/// <summary>macOS 平台的逻辑 AppHint → 可观测 bundle identity 映射。</summary>
public sealed class MacCollectorAppHintResolver : ICollectorAppHintResolver
{
    public CollectorAppHintResolution Resolve(string appHint) => appHint switch
    {
        "chrome" => CollectorAppHintResolution.Resolved("mac:com.google.chrome"),
        "edge" => CollectorAppHintResolution.Resolved("mac:com.microsoft.edgemac"),
        "brave" => CollectorAppHintResolution.Resolved("mac:com.brave.browser"),
        "opera" => CollectorAppHintResolution.Resolved("mac:com.operasoftware.opera"),
        "vivaldi" => CollectorAppHintResolution.Resolved("mac:com.vivaldi.vivaldi"),
        "firefox" => CollectorAppHintResolution.Resolved("mac:org.mozilla.firefox"),
        _ => CollectorAppHintResolution.Unknown
    };
}
