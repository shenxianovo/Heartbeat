using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop;

/// <summary>
/// 把桌面观测投影成 Record。前台应用与前台窗口是两个观测对象，各自成 Track；
/// 一个观测对象在读数不变且确认没有中断的整段时间里只有一条 Record，原生通知本身不切分区间。
/// </summary>
internal sealed class DesktopRecordProjector(
    string collectorKey,
    string target,
    string displayName,
    TimeSpan maximumConfirmationGap,
    TimeSpan windowTitleDwell,
    Action<SubmissionRoute, RecordSnapshot> stage)
{
    private readonly CollectorDeclaration _collector = new(collectorKey, target, displayName);
    private readonly Dictionary<DesktopAwayReason, CurrentRange> _away = [];
    private readonly Dictionary<ObservationCapability, (CapabilityObservation Value, CurrentRange Range)> _statuses = [];
    private readonly HashSet<int> _heldKeys = [];
    private CurrentRange? _application;
    private ForegroundApplication? _applicationValue;
    private CurrentRange? _window;
    private string? _windowValue;
    private PendingTitle? _pendingTitle;
    private DateTimeOffset? _lastActivityConfirmation;

    public void Apply(DesktopObservation observation, DateTimeOffset at)
    {
        switch (observation)
        {
            case DesktopObservation.Activity activity:
                ObserveActivity(activity.Sample, at);
                break;
            case DesktopObservation.AwayEntered away:
                EnterAway(away.Reason, at);
                break;
            case DesktopObservation.AwayExited away:
                ExitAway(away.Reason, away.CurrentActivity, at);
                break;
            case DesktopObservation.Input input:
                ObserveInput(input.Value, at);
                break;
            case DesktopObservation.Capability capability:
                ObserveCapability(capability.Value, at);
                break;
        }
    }

    public void Confirm(DesktopSnapshot snapshot, DateTimeOffset at)
    {
        foreach (var capability in snapshot.Capabilities)
        {
            ObserveCapability(capability, at);
        }

        ObserveActivity(snapshot.Activity, at);
    }

    private void ObserveActivity(DesktopActivitySample? sample, DateTimeOffset at)
    {
        if (_away.Count != 0)
        {
            return;
        }

        if (sample is null)
        {
            BreakActivityAt(at);
            return;
        }

        if (!HasTimelyConfirmation(at))
        {
            // 上一次确认之外的时间没有观测，已有区间不能跨过这段空白继续。
            _application = null;
            _applicationValue = null;
            _window = null;
            _windowValue = null;
            _pendingTitle = null;
        }

        var applicationChanged = _applicationValue != sample.Application;
        if (_application is null || applicationChanged)
        {
            ExtendApplication(at);
            _application = CurrentRange.Start(at);
            _applicationValue = sample.Application;
        }

        ObserveWindowTitle(sample.WindowTitle, applicationChanged, at);

        _lastActivityConfirmation = at;
        ExtendApplication(at);
    }

    /// <summary>
    /// 新标题站稳后才写入，区间从首次出现时开始；应用切换时的标题立即承认。
    /// </summary>
    private void ObserveWindowTitle(string? title, bool applicationChanged, DateTimeOffset at)
    {
        SettlePendingTitle(at);

        if (title is null)
        {
            // 标题读不到只结束窗口区间，前台应用照旧连续。
            ExtendWindow(at);
            _window = null;
            _windowValue = null;
            _pendingTitle = null;
            return;
        }

        if (_window is null || applicationChanged)
        {
            ExtendWindow(at);
            _pendingTitle = null;
            _window = CurrentRange.Start(at);
            _windowValue = title;
            ExtendWindow(at);
            return;
        }

        if (_windowValue == title)
        {
            _pendingTitle = null;
            ExtendWindow(at);
            return;
        }

        if (_pendingTitle?.Title != title)
        {
            // 前一区间吸收未站稳的旧候选，并延长到新候选的起点。
            _pendingTitle = new PendingTitle(title, at);
            ExtendWindow(at);
        }
    }

    /// <summary>
    /// 将已站稳的候选写为 Record，区间从候选首次出现时开始。
    /// </summary>
    private void SettlePendingTitle(DateTimeOffset at)
    {
        if (_pendingTitle is not { } pending || at - pending.Since < windowTitleDwell)
        {
            // 保留候选起点，按首次出现时间判断是否站稳。
            return;
        }

        _pendingTitle = null;
        if (!HasTimelyConfirmation(at))
        {
            return;
        }

        ExtendWindow(pending.Since);
        _window = CurrentRange.Start(pending.Since);
        _windowValue = pending.Title;
    }

    private void EnterAway(DesktopAwayReason reason, DateTimeOffset at)
    {
        if (_away.ContainsKey(reason))
        {
            return;
        }

        BreakActivityAt(at);
        var range = CurrentRange.Start(at);
        _away.Add(reason, range);
        StageRange(DesktopProtocols.Away, range, at, DesktopProtocols.AwayValue(target, reason));
    }

    private void ExitAway(DesktopAwayReason reason, DesktopActivitySample? current, DateTimeOffset at)
    {
        if (!_away.Remove(reason, out var range))
        {
            return;
        }

        StageRange(DesktopProtocols.Away, range, at, DesktopProtocols.AwayValue(target, reason));
        if (_away.Count == 0)
        {
            ObserveActivity(current, at);
        }
    }

    private void ObserveInput(DesktopInputObservation input, DateTimeOffset at)
    {
        switch (input.Kind)
        {
            case DesktopInputKind.KeyDown:
                if (!_heldKeys.Add(input.Code))
                {
                    return;
                }
                break;
            case DesktopInputKind.KeyUp:
                _heldKeys.Remove(input.Code);
                return;
        }

        var id = Guid.CreateVersion7(at);
        stage(Route(DesktopProtocols.Input), new RecordSnapshot(
            id, at, null, null, DesktopProtocols.InputValue(target, input)));
    }

    private void ObserveCapability(CapabilityObservation value, DateTimeOffset at)
    {
        if (_statuses.TryGetValue(value.Capability, out var current) && current.Value == value)
        {
            StageRange(DesktopProtocols.ObservationStatus, current.Range, at, DesktopProtocols.StatusValue(target, value));
            return;
        }

        if (current.Value is not null)
        {
            StageRange(DesktopProtocols.ObservationStatus, current.Range, at,
                DesktopProtocols.StatusValue(target, current.Value));
        }

        var next = CurrentRange.Start(at);
        _statuses[value.Capability] = (value, next);
        StageRange(DesktopProtocols.ObservationStatus, next, at, DesktopProtocols.StatusValue(target, value));

        if (value.State == ObservationState.Available)
        {
            return;
        }

        switch (value.Capability)
        {
            // 应用身份未知时，窗口观测也失效。
            case ObservationCapability.Application:
                BreakActivityAt(at);
                break;
            // 标题观测失败只中断窗口区间。
            case ObservationCapability.WindowTitle:
                BreakWindowAt(at);
                break;
            case ObservationCapability.Input:
                _heldKeys.Clear();
                break;
        }
    }

    private void ExtendApplication(DateTimeOffset at)
    {
        if (_application is not { } range || _applicationValue is not { } value)
        {
            return;
        }

        StageRange(DesktopProtocols.Application, range, at, DesktopProtocols.ApplicationValue(target, value));
    }

    private void ExtendWindow(DateTimeOffset at)
    {
        if (_window is not { } range || _windowValue is not { } title)
        {
            return;
        }

        StageRange(DesktopProtocols.Window, range, at, DesktopProtocols.WindowValue(target, title));
    }

    private void BreakActivityAt(DateTimeOffset at)
    {
        BreakWindowAt(at);
        if (HasTimelyConfirmation(at))
        {
            ExtendApplication(at);
        }

        _application = null;
        _applicationValue = null;
        _lastActivityConfirmation = null;
    }

    private void BreakWindowAt(DateTimeOffset at)
    {
        // 中断前先补记已站稳的候选。
        SettlePendingTitle(at);
        if (HasTimelyConfirmation(at))
        {
            ExtendWindow(at);
        }

        _window = null;
        _windowValue = null;
        _pendingTitle = null;
    }

    private bool HasTimelyConfirmation(DateTimeOffset at)
    {
        if (_lastActivityConfirmation is not { } previous)
        {
            return false;
        }

        var elapsed = at - previous;
        return elapsed >= TimeSpan.Zero && elapsed <= maximumConfirmationGap;
    }

    private void StageRange(TrackDeclaration track, CurrentRange range, DateTimeOffset endedAt, System.Text.Json.JsonElement value) =>
        stage(Route(track), new RecordSnapshot(range.Id, range.StartedAt, endedAt, null, value));

    private SubmissionRoute Route(TrackDeclaration track) => new(_collector, track);

    private sealed record CurrentRange(Guid Id, DateTimeOffset StartedAt)
    {
        public static CurrentRange Start(DateTimeOffset at) => new(Guid.CreateVersion7(at), at);
    }

    /// <summary>还没站稳的标题候选，以及它第一次出现的时刻。</summary>
    private sealed record PendingTitle(string Title, DateTimeOffset Since);
}
