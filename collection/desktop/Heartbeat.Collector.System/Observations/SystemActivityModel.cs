using Heartbeat.Core;

namespace Heartbeat.Collector.System.Observations;

/// <summary>
/// 一个本机桌面的活动观测模型。应用与标题是桌面活动的读数，不是另行登记的观测对象。
/// 只判定业务转场；时间、Fact 身份、快照及交付由调用方管理。调用方串行访问此模型。
/// </summary>
internal sealed class SystemActivityModel
{
    private bool _isAway;
    private string? _observedTitle;

    // 未通过点击门控的标题只更新采样状态，保留当前活动起始时的原始标题。
    public DesktopActivity Current { get; private set; } = DesktopActivity.None;

    public void Start(DesktopActivity initial, IReadOnlyList<string> awayProcessNames)
    {
        _isAway = false;
        SetCurrent(Normalize(initial, awayProcessNames));
    }

    /// <returns>是否发生业务转场。明确的 App 激活或窗口切换即使读数相同也可以是转场。</returns>
    public bool Observe(
        DesktopObservation observation,
        bool clickedRecently,
        bool splitFocusedWindowChangesUnconditionally,
        IReadOnlyList<string> awayProcessNames)
    {
        switch (observation.Kind)
        {
            case DesktopObservationKind.EnteredAway:
                if (_isAway) return false;
                _isAway = true;
                SetCurrent(new DesktopActivity(AppIdentityKeys.Away, "离开", null));
                return true;

            case DesktopObservationKind.ExitedAway:
                if (!_isAway) return false;
                _isAway = false;
                SetCurrent(Normalize(observation.Activity, awayProcessNames));
                return true;

            case DesktopObservationKind.AppActivated:
            case DesktopObservationKind.FocusedWindowChanged:
            case DesktopObservationKind.TitleChanged:
                if (_isAway) return false;
                var next = Normalize(observation.Activity, awayProcessNames);
                var appSame = string.Equals(Current.AppIdentityKey, next.AppIdentityKey,
                    StringComparison.OrdinalIgnoreCase);
                var gateTitle = observation.Kind == DesktopObservationKind.TitleChanged
                    || (observation.Kind == DesktopObservationKind.FocusedWindowChanged
                        && !splitFocusedWindowChangesUnconditionally && appSame);
                if (gateTitle && appSame)
                {
                    if (string.Equals(_observedTitle, next.Title, StringComparison.Ordinal))
                        return false;
                    if (!clickedRecently)
                    {
                        _observedTitle = next.Title;
                        return false;
                    }
                }
                SetCurrent(next);
                return true;

            default:
                return false;
        }
    }

    private void SetCurrent(DesktopActivity activity)
    {
        Current = activity;
        _observedTitle = activity.Title;
    }

    private static DesktopActivity Normalize(DesktopActivity activity, IReadOnlyList<string> awayProcessNames)
    {
        var key = activity.AppIdentityKey;
        if (string.IsNullOrEmpty(key)) return activity;
        foreach (var name in awayProcessNames)
        {
            if (string.Equals(key, AppIdentityKeys.FromLegacyWindowsAppName(name),
                    StringComparison.OrdinalIgnoreCase))
                return activity with { AppIdentityKey = AppIdentityKeys.Away };
        }
        return activity with { AppIdentityKey = AppIdentityKeys.Normalize(key) };
    }
}
