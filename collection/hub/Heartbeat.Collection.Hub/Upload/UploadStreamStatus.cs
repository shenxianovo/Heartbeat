namespace Heartbeat.Collection.Hub.Upload;

public enum UploadStreamState
{
    Ready,
    Backlog,
    Backpressure,
    GapRecorded,
    UpdateRequired,
    CacheMigrationFailed,
    CacheWriteFailed,
    DeadLetterWriteFailed
}

/// <summary>Presentation-facing status for one Upload Stream.</summary>
public sealed record UploadStreamStatus(
    UploadStreamState State,
    string? Message = null,
    string Action = "",
    int DeadLetterCount = 0,
    string? RecoveryPath = null,
    string? DeadLetterPath = null)
{
    public static UploadStreamStatus Ready { get; } = new(UploadStreamState.Ready);
}
