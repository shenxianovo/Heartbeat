namespace Heartbeat.Collector.Desktop;

/// <summary>
/// 前台应用的读数。只保留进入 Record 的身份字段，进程号这类同一应用会变化的运行时细节不属于读数。
/// </summary>
public sealed record ForegroundApplication(
    string Platform,
    string IdKind,
    string Id,
    string? DisplayName = null);
