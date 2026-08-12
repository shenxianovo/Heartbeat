namespace Heartbeat.Desktop.UI.ViewModels;

public enum OperationalNoticeKind
{
    UpdateRequired,
    CacheMigrationFailed,
    CacheWriteFailed,
    DeadLetterWriteFailed,
    DeadLettersAvailable
}

public sealed record OperationalNoticeViewModel(
    OperationalNoticeKind Kind,
    string Title,
    string Message,
    string? Action = null,
    string? Path = null);
