namespace Heartbeat.Desktop.Core.Observations;

/// <summary>平台 adapter 对当前桌面活动的一次语义采样。</summary>
public readonly record struct DesktopActivity(string? AppName, string? Title)
{
    public static readonly DesktopActivity None = new(null, null);
}

/// <summary>
/// 平台 adapter 必须保留的转场种类。App 激活与 focused-window 切换始终切段；
/// 只有同窗标题变化进入 Interaction Signal 门控（ADR-016/033）。
/// </summary>
public enum DesktopObservationKind
{
    AppActivated,
    FocusedWindowChanged,
    TitleChanged,
    EnteredAway,
    ExitedAway,
}

public readonly record struct DesktopObservation(
    DesktopObservationKind Kind,
    DesktopActivity Activity)
{
    public static DesktopObservation AppActivated(DesktopActivity activity)
        => new(DesktopObservationKind.AppActivated, activity);

    public static DesktopObservation FocusedWindowChanged(DesktopActivity activity)
        => new(DesktopObservationKind.FocusedWindowChanged, activity);

    public static DesktopObservation TitleChanged(DesktopActivity activity)
        => new(DesktopObservationKind.TitleChanged, activity);

    public static DesktopObservation EnteredAway()
        => new(DesktopObservationKind.EnteredAway, DesktopActivity.None);

    public static DesktopObservation ExitedAway(DesktopActivity activity)
        => new(DesktopObservationKind.ExitedAway, activity);
}

/// <summary>
/// 平台观测 seam。Windows/macOS adapter 负责把原生回调翻译成语义观察；
/// Desktop.Core 只认识这里的事实，不认识窗口句柄、通知名或原生权限。
/// </summary>
public interface IDesktopObservationSource
{
    event Action<DesktopObservation>? Observation;

    DesktopActivity CurrentActivity { get; }

    void Start();
    void Stop();
}
