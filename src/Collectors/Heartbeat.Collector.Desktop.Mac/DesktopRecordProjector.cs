using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac;

/// <summary>
/// 把桌面观测投影成 Record。前台应用与前台窗口是两个观测对象，各自成 Track；
/// 一个观测对象在读数不变且确认没有中断的整段时间里只有一条 Record，原生通知本身不切分区间。
/// </summary>
internal sealed class DesktopRecordProjector(
    string target,
    string displayName,
    TimeSpan maximumConfirmationGap,
    TimeSpan windowTitleDwell,
    Action<SubmissionRoute, RecordSnapshot> stage)
{
    private readonly CollectorDeclaration _collector = new(DesktopProtocols.CollectorKey, target, displayName);
    private readonly Dictionary<MacAwayReason, CurrentRange> _away = [];
    private readonly Dictionary<ObservationCapability, (CapabilityObservation Value, CurrentRange Range)> _statuses = [];
    private readonly HashSet<int> _heldKeys = [];
    private CurrentRange? _application;
    private ForegroundApplication? _applicationValue;
    private CurrentRange? _window;
    private string? _windowValue;
    private PendingTitle? _pendingTitle;
    private DateTimeOffset? _lastActivityConfirmation;

    public void Apply(MacSystemObservation observation, DateTimeOffset at)
    {
        switch (observation)
        {
            case MacSystemObservation.Activity activity:
                ObserveActivity(activity.Sample, at);
                break;
            case MacSystemObservation.AwayEntered away:
                EnterAway(away.Reason, at);
                break;
            case MacSystemObservation.AwayExited away:
                ExitAway(away.Reason, away.CurrentActivity, at);
                break;
            case MacSystemObservation.Input input:
                ObserveInput(input.Value, at);
                break;
            case MacSystemObservation.Capability capability:
                ObserveCapability(capability.Value, at);
                break;
        }
    }

    public void Confirm(MacSystemSnapshot snapshot, DateTimeOffset at)
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
    /// 标题变化要先站稳一段时间才承认：滚动字幕、终端 spinner 与导航中间态都活不过这段时间。
    /// 承认时 Record 从这个标题第一次出现的那一刻算起，静置只推迟写入，不改区间。
    /// 应用切换那一刻的标题属于新窗口，没有可等的余地，直接承认。
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
            // 换了个候选：上一个没站稳，被前一个区间吸收。旧 Record 先延长到这一刻，
            // 新候选站稳后它的终点就定在这里。
            _pendingTitle = new PendingTitle(title, at);
            ExtendWindow(at);
        }
    }

    /// <summary>
    /// 候选标题活过静置时间就转正，Record 从它出现的那一刻起算；没活过就被前一个区间吸收。
    /// </summary>
    private void SettlePendingTitle(DateTimeOffset at)
    {
        if (_pendingTitle is not { } pending || at - pending.Since < windowTitleDwell)
        {
            // 还没站稳的候选继续挂着，它的起点必须保留：站稳与否要按第一次出现算，不是按最近一次读数算。
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

    private void EnterAway(MacAwayReason reason, DateTimeOffset at)
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

    private void ExitAway(MacAwayReason reason, DesktopActivitySample? current, DateTimeOffset at)
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
            // 前台应用观测失败时连应用是谁都不知道，窗口读数也失去依据。
            case ObservationCapability.Application:
                BreakActivityAt(at);
                break;
            // 标题观测只服务窗口这一个观测对象，失败不影响前台应用。
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
        // 候选到这一刻为止一直是它，站得住就先转正，别把一段真实的标题连同中断一起丢掉。
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
