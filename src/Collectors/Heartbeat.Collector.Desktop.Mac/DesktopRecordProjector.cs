using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac;

internal sealed class DesktopRecordProjector(
    string target,
    string displayName,
    TimeSpan maximumConfirmationGap,
    Action<SubmissionRoute, RecordSnapshot> stage)
{
    private readonly CollectorDeclaration _collector = new(DesktopProtocols.CollectorKey, target, displayName);
    private readonly Dictionary<MacAwayReason, CurrentRange> _away = [];
    private readonly Dictionary<ObservationCapability, (CapabilityObservation Value, CurrentRange Range)> _statuses = [];
    private readonly HashSet<int> _heldKeys = [];
    private CurrentRange? _application;
    private DesktopActivitySample? _applicationValue;
    private DateTimeOffset? _lastApplicationConfirmation;

    public void Apply(MacSystemObservation observation, DateTimeOffset at)
    {
        switch (observation)
        {
            case MacSystemObservation.Activity activity:
                ObserveActivity(activity.Sample, activity.Kind, at);
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

        ObserveActivity(snapshot.Activity, ActivityChangeKind.Confirmation, at);
    }

    private void ObserveActivity(DesktopActivitySample? sample, ActivityChangeKind kind, DateTimeOffset at)
    {
        if (_away.Count != 0)
        {
            return;
        }

        var delayed = !HasTimelyApplicationConfirmation(at);
        if (sample is null)
        {
            BreakApplicationAt(at);
            return;
        }

        var transition = kind != ActivityChangeKind.Confirmation;
        if (delayed)
        {
            _application = null;
            _applicationValue = null;
        }

        if (_application is null || transition || _applicationValue != sample)
        {
            if (!delayed)
            {
                ExtendApplication(at);
            }

            _application = CurrentRange.Start(at);
            _applicationValue = sample;
        }

        _lastApplicationConfirmation = at;
        if (HasTimelyApplicationConfirmation(at))
        {
            ExtendApplication(at);
        }
    }

    private void EnterAway(MacAwayReason reason, DateTimeOffset at)
    {
        if (_away.ContainsKey(reason))
        {
            return;
        }

        BreakApplicationAt(at);
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
            ObserveActivity(current, ActivityChangeKind.Recovery, at);
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

        if (value.Capability is ObservationCapability.Application or ObservationCapability.WindowTitle
            && value.State != ObservationState.Available)
        {
            BreakApplicationAt(at);
        }
        if (value.Capability == ObservationCapability.Input && value.State != ObservationState.Available)
        {
            _heldKeys.Clear();
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

    private void BreakApplication()
    {
        _application = null;
        _applicationValue = null;
        _lastApplicationConfirmation = null;
    }

    private void BreakApplicationAt(DateTimeOffset at)
    {
        if (HasTimelyApplicationConfirmation(at))
        {
            ExtendApplication(at);
        }
        BreakApplication();
    }

    private bool HasTimelyApplicationConfirmation(DateTimeOffset at)
    {
        if (_lastApplicationConfirmation is not { } previous)
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
}
