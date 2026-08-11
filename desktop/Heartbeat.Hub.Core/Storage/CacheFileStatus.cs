namespace Heartbeat.Hub.Core.Storage;

public enum CacheFileState
{
    Ready,
    Migrated,
    MigrationFailed
}

/// <summary>可由桌面 presentation 读取的缓存启动状态。</summary>
public sealed record CacheFileStatus(
    CacheFileState State,
    string? Message = null,
    string Action = "",
    string? BackupPath = null)
{
    public static CacheFileStatus Ready { get; } = new(CacheFileState.Ready);
}

public sealed class CacheUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
