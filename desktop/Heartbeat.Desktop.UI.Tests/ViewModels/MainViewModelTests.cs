using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.ViewModels;
using Heartbeat.Hub.Core.Presence;
using Heartbeat.Hub.Core.Http;
using Heartbeat.Hub.Core.Upload;
using Heartbeat.Desktop.UI.Logging;
using Serilog.Events;

namespace Heartbeat.Desktop.UI.Tests.ViewModels;

public sealed class MainViewModelTests
{
    [Fact]
    public void CurrentActivity_IsPresentedWithoutCreatingANativeWindow()
    {
        var state = new FakeDesktopState
        {
            Current = DesktopStateSnapshot.Empty with
            {
                CurrentActivity = new CurrentActivity("win:code", "Visual Studio Code")
            }
        };

        using var viewModel = TestViewModel.Create(state);

        Assert.Equal("Visual Studio Code", viewModel.CurrentApp);
    }

    [Fact]
    public void CollectorPanel_PresentsSystemAsReadOnlyAndDerivesPluginActivityFromFlushPeriod()
    {
        var now = new DateTimeOffset(2026, 8, 12, 1, 0, 0, TimeSpan.Zero);
        var state = new FakeDesktopState
        {
            Current = DesktopStateSnapshot.Empty with
            {
                Collectors = new Dictionary<string, CollectorRegistrationState>
                {
                    ["browser"] = new(true, 30_000)
                },
                SourceLastSeen = new Dictionary<string, DateTimeOffset>
                {
                    ["browser"] = now.AddSeconds(-80)
                }
            }
        };
        var scheduler = new ManualPresentationScheduler { UtcNow = now };

        using var viewModel = TestViewModel.Create(state, scheduler: scheduler);

        var system = Assert.Single(viewModel.Collectors, item => item.Source == "system");
        Assert.True(system.IsActive);
        Assert.False(system.CanToggle);

        var browser = Assert.Single(viewModel.Collectors, item => item.Source == "browser");
        Assert.True(browser.IsActive);
        Assert.True(browser.CanToggle);
        Assert.True(browser.Enabled);

        browser.Enabled = false;
        Assert.Equal(("browser", false), state.LastCollectorValue);
    }

    [Fact]
    public void SystemCollector_PresentsLocalInteractionSignalSeparatelyFromDurableInputRecording()
    {
        var state = new FakeDesktopState
        {
            Current = DesktopStateSnapshot.Empty with
            {
                Settings = DesktopSettingsSnapshot.Default with
                {
                    InputEventRecordingEnabled = false
                }
            }
        };

        using var viewModel = TestViewModel.Create(state);

        var system = Assert.Single(viewModel.Collectors, item => item.IsSystem);
        Assert.Equal("仅本地，不保存、不上传", system.InteractionSignalDescription);
        Assert.True(system.CanToggleRecording);
        Assert.False(system.RecordingEnabled);

        system.RecordingEnabled = true;

        Assert.Equal(true, state.LastInputEventRecordingValue);
    }

    [Fact]
    public void PortableCoreFailures_ArePresentedAsActionableOperationalNotices()
    {
        var state = new FakeDesktopState
        {
            Current = DesktopStateSnapshot.Empty with
            {
                Compatibility = new ClientCompatibilitySnapshot(true, "请安装新版本"),
                UploadStreams = new Dictionary<string, UploadStreamStatus>
                {
                    ["段"] = new(
                        UploadStreamState.CacheMigrationFailed,
                        "旧缓存迁移失败",
                        "从备份恢复后重试",
                        RecoveryPath: "/tmp/segments.backup"),
                    ["输入事件"] = new(
                        UploadStreamState.Ready,
                        DeadLetterCount: 2,
                        DeadLetterPath: "/tmp/input-dead-letter.json")
                }
            }
        };

        using var viewModel = TestViewModel.Create(state);

        Assert.Contains(viewModel.OperationalNotices, notice =>
            notice.Kind == OperationalNoticeKind.UpdateRequired &&
            notice.Message == "请安装新版本");
        Assert.Contains(viewModel.OperationalNotices, notice =>
            notice.Kind == OperationalNoticeKind.CacheMigrationFailed &&
            notice.Path == "/tmp/segments.backup");
        Assert.Contains(viewModel.OperationalNotices, notice =>
            notice.Kind == OperationalNoticeKind.DeadLettersAvailable &&
            notice.Message.Contains("2", StringComparison.Ordinal) &&
            notice.Path == "/tmp/input-dead-letter.json");
    }

    [Fact]
    public void ClosingSettings_HidesTheWindowWithoutStoppingTheAgent()
    {
        var window = new FakeWindowController();
        using var viewModel = TestViewModel.Create(window: window);

        viewModel.CloseSettingsCommand.Execute(null);

        Assert.Equal(1, window.HideCount);
    }

    [Fact]
    public async Task UpdateApplication_IsGatedUntilTheDownloadIsReady()
    {
        var updates = new FakeUpdateController();
        using var viewModel = TestViewModel.Create(updates: updates);

        Assert.False(viewModel.ApplyUpdateCommand.CanExecute(null));

        updates.Publish(new UpdateSnapshot(UpdateState.ReadyToApply, "2.0.0"));

        Assert.True(viewModel.ApplyUpdateCommand.CanExecute(null));
        await viewModel.ApplyUpdateCommand.ExecuteAsync(null);
        Assert.Equal(1, updates.ApplyCount);
    }

    [Fact]
    public void Settings_SaveConnectionValuesAndLoginStartThroughThePlatformSeam()
    {
        var state = new FakeDesktopState
        {
            Current = DesktopStateSnapshot.Empty with
            {
                Settings = new DesktopSettingsSnapshot("old-key", "Workstation", 2, true),
                LoginStartEnabled = false
            }
        };
        using var viewModel = TestViewModel.Create(state);

        viewModel.ApiKey = " new-key ";
        viewModel.DeviceName = " Desktop ";
        viewModel.UploadIntervalMinutes = "5";
        viewModel.SaveConfigCommand.Execute(null);
        viewModel.LoginStartEnabled = true;

        Assert.Equal(new DesktopSettingsInput("new-key", "Desktop", 5), state.LastSettings);
        Assert.Equal(true, state.LastLoginStartValue);
    }

    [Fact]
    public void CapabilityStates_ArePresentedFromThePlatformSnapshot()
    {
        var state = new FakeDesktopState
        {
            Current = DesktopStateSnapshot.Empty with
            {
                Capabilities = new DesktopCapabilitySnapshot(
                    CapabilityAvailability.Available,
                    CapabilityAvailability.PermissionRequired,
                    CapabilityAvailability.PermissionRequired,
                    CapabilityAvailability.Unavailable,
                    "需要授权后才能采集更深层活动")
            }
        };

        using var viewModel = TestViewModel.Create(state);

        Assert.Contains(viewModel.Capabilities, capability =>
            capability.Name == "前台应用" && capability.Availability == CapabilityAvailability.Available);
        Assert.Contains(viewModel.Capabilities, capability =>
            capability.Name == "交互信号" && capability.Availability == CapabilityAvailability.PermissionRequired);
        Assert.Equal("需要授权后才能采集更深层活动", viewModel.CapabilityMessage);
    }

    [Fact]
    public void LogPresentation_FiltersExistingEntriesByTheSelectedLevel()
    {
        var logs = new FakeLogFeed(
        [
            new LogEntry("debug detail", LogEventLevel.Debug),
            new LogEntry("agent started", LogEventLevel.Information),
            new LogEntry("upload warning", LogEventLevel.Warning)
        ]);

        using var viewModel = TestViewModel.Create(logs: logs);

        Assert.DoesNotContain("debug detail", viewModel.LogText);
        Assert.Contains("agent started", viewModel.LogText);
        Assert.Contains("upload warning", viewModel.LogText);
    }

    [Theory]
    [InlineData(UpdateCheckResult.UpToDate, "当前已是最新版本")]
    [InlineData(UpdateCheckResult.UpdateFound, "发现新版本")]
    [InlineData(UpdateCheckResult.CheckFailed, "检查更新失败")]
    public async Task UpdateCheck_PresentsUpToDateFoundAndFailureAsDifferentResults(
        UpdateCheckResult result,
        string expectedMessage)
    {
        var updates = new FakeUpdateController { CheckResult = result };
        using var viewModel = TestViewModel.Create(updates: updates);

        await viewModel.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.Equal(result, viewModel.LastUpdateCheckResult);
        Assert.Contains(expectedMessage, viewModel.UpdateCheckMessage);
    }
}
