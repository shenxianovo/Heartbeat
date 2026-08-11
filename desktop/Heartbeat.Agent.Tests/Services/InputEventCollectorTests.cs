using Heartbeat.Agent.Services;
using Heartbeat.Agent.Utils;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Desktop.Core.Collection;
using Heartbeat.Desktop.Core.Input;
using Heartbeat.Hub.Core.Configuration;
using Heartbeat.Hub.Core.Time;

namespace Heartbeat.Agent.Tests.Services;

public sealed class InputEventCollectorTests
{
    private sealed class FakeHook : ILowLevelInputHook
    {
        public event Action<WindowsNativeKeyObservation>? KeyDown;
        public event Action<WindowsNativeKeyObservation>? KeyUp;
        public event Action<short>? MouseButton;
        public event Action<int>? Scroll;
        public void StartHook() { }
        public void StopHook() { }
        public void RaiseKeyDown(WindowsNativeKeyObservation value) => KeyDown?.Invoke(value);
        public void RaiseKeyUp(WindowsNativeKeyObservation value) => KeyUp?.Invoke(value);
        public void RaiseMouse(short value) => MouseButton?.Invoke(value);
        public void RaiseScroll(int value) => Scroll?.Invoke(value);
    }

    private sealed class FakeSignal : IInputActivitySignal
    {
        public int Clicks { get; private set; }
        public void MarkClick() => Clicks++;
        public bool ClickedWithin(TimeSpan window) => Clicks > 0;
    }

    private sealed class MutablePolicy(bool enabled) : IInputEventRecordingPolicy
    {
        public bool Enabled { get; private set; } = enabled;
        public event Action<bool>? Changed;
        public void Set(bool value)
        {
            Enabled = value;
            Changed?.Invoke(value);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-08-11T00:00:00Z");
    }

    [Fact]
    public async Task RecordingDisabled_KeepsInteractionSignalButProducesNoDurableEvents()
    {
        var hook = new FakeHook();
        var signal = new FakeSignal();
        var policy = new MutablePolicy(false);
        var buffer = new InputEventBuffer(new FixedClock());
        using var collector = new InputEventCollector(hook, signal, policy, buffer);
        await collector.StartAsync(CancellationToken.None);

        hook.RaiseMouse(1);
        hook.RaiseScroll(120);
        hook.RaiseKeyDown(new WindowsNativeKeyObservation(0x41, 0x1E, false));

        Assert.Equal(1, signal.Clicks);
        Assert.Empty(buffer.DrainAll());
    }

    [Fact]
    public async Task DisablingRecording_ResetsHeldState_WithoutStoppingHook()
    {
        var hook = new FakeHook();
        var policy = new MutablePolicy(true);
        var buffer = new InputEventBuffer(new FixedClock());
        using var collector = new InputEventCollector(hook, new FakeSignal(), policy, buffer);
        await collector.StartAsync(CancellationToken.None);
        var keyA = new WindowsNativeKeyObservation(0x41, 0x1E, false);

        hook.RaiseKeyDown(keyA);
        policy.Set(false);
        hook.RaiseKeyUp(keyA); // recording=false 时仍允许清理 held state
        policy.Set(true);
        hook.RaiseKeyDown(keyA);

        var events = buffer.DrainAll();
        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.Equal(InputCodeSets.HeartbeatKeyPositionV1, item.CodeSet));
        Assert.All(events, item => Assert.Equal((short)InputKeyPosition.KeyA, item.Code));
    }
}
