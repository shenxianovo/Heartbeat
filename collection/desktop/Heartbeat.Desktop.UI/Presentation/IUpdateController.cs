namespace Heartbeat.Desktop.UI.Presentation;

public enum UpdateState
{
    Idle,
    UpdateAvailable,
    Downloading,
    ReadyToApply
}

public enum UpdateCheckResult
{
    UpToDate,
    UpdateFound,
    CheckFailed,
    Skipped
}

public sealed record UpdateSnapshot(
    UpdateState State,
    string? Version = null,
    int? DownloadProgress = null,
    string? Error = null)
{
    public static UpdateSnapshot Idle { get; } = new(UpdateState.Idle);
}

public interface IUpdateController
{
    bool IsSupported { get; }
    UpdateSnapshot Current { get; }
    event Action<UpdateSnapshot>? Changed;
    Task<UpdateCheckResult> CheckAsync();
    Task<bool> ApplyAsync();
}
