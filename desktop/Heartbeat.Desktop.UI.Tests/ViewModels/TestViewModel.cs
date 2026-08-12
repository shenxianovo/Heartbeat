using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.ViewModels;

namespace Heartbeat.Desktop.UI.Tests.ViewModels;

internal static class TestViewModel
{
    public static MainViewModel Create(
        IDesktopState? state = null,
        IUpdateController? updates = null,
        IWindowController? window = null,
        IPresentationScheduler? scheduler = null,
        ILogFeed? logs = null) =>
        new(
            state ?? new FakeDesktopState(),
            updates ?? new FakeUpdateController(),
            window ?? new FakeWindowController(),
            scheduler ?? new ManualPresentationScheduler(),
            logs ?? new FakeLogFeed());
}

internal sealed class FakeDesktopState : IDesktopState
{
    public DesktopStateSnapshot Current { get; set; } = DesktopStateSnapshot.Empty;
    public bool? LastInputEventRecordingValue { get; private set; }
    public DesktopSettingsInput? LastSettings { get; private set; }
    public bool? LastLoginStartValue { get; private set; }
    public (string Source, bool Enabled)? LastCollectorValue { get; private set; }
    public event Action<DesktopStateSnapshot>? Changed;

    public void SaveSettings(DesktopSettingsInput settings) => LastSettings = settings;
    public void SetCollectorEnabled(string source, bool enabled) => LastCollectorValue = (source, enabled);
    public void SetInputEventRecordingEnabled(bool enabled) => LastInputEventRecordingValue = enabled;
    public void SetLoginStartEnabled(bool enabled) => LastLoginStartValue = enabled;

    public void Publish(DesktopStateSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }
}

internal sealed class FakeUpdateController : IUpdateController
{
    public UpdateSnapshot Current { get; set; } = UpdateSnapshot.Idle;
    public int ApplyCount { get; private set; }
    public UpdateCheckResult CheckResult { get; set; } = UpdateCheckResult.UpToDate;
    public event Action<UpdateSnapshot>? Changed;
    public Task<UpdateCheckResult> CheckAsync() => Task.FromResult(CheckResult);
    public Task<bool> ApplyAsync()
    {
        ApplyCount++;
        return Task.FromResult(Current.State == UpdateState.ReadyToApply);
    }
    public void Publish(UpdateSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }
}

internal sealed class FakeWindowController : IWindowController
{
    public int HideCount { get; private set; }
    public void HideSettings() => HideCount++;
}

internal sealed class ManualPresentationScheduler : IPresentationScheduler
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    public void Post(Action action) => action();
    public IDisposable SchedulePeriodic(TimeSpan interval, Action action) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

internal sealed class FakeLogFeed : ILogFeed
{
    private readonly IReadOnlyList<LogEntry> _entries;

    public FakeLogFeed(IReadOnlyList<LogEntry>? entries = null)
    {
        _entries = entries ?? [];
    }

    public int Capacity => 200;
    public event Action<IReadOnlyList<LogEntry>>? Changed;
    public IReadOnlyList<LogEntry> GetAll() => _entries;
    public void Publish(IReadOnlyList<LogEntry> entries) => Changed?.Invoke(entries);
}
