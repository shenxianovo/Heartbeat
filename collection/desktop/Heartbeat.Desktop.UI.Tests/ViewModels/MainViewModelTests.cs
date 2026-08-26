using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.ViewModels;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Upload;
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
    public void BrowserCollector_PresentsOneUserStatusAndOpensSetupFromBrowserIcons()
    {
        var state = new FakeDesktopState
        {
            Current = DesktopStateSnapshot.Empty with
            {
                BrowserCollector = new BrowserCollectorState(
                    true,
                    "0.2.0",
                    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "/data/packages/browser/0.2.0",
                    "/data/packages/browser/0.2.0/browser-extension",
                    true,
                    ExternalHostRuntimeStatus.Degraded,
                    "浏览器仍在运行旧版本；请重新加载。",
                    true,
                    "0.1.0")
            }
        };
        var window = new FakeWindowController();
        using var viewModel = TestViewModel.Create(state, window: window);

        var browser = Assert.Single(viewModel.Collectors, item => item.Source == "browser");
        Assert.True(browser.IsPackageInstalled);
        Assert.Equal("0.2.0", browser.PackageVersion);
        Assert.Equal("需要修复", browser.BrowserStatusText);
        Assert.True(browser.IsBrowserDegraded);
        Assert.Contains("重新加载", browser.RuntimeStatusDetail);
        Assert.Equal("/data/packages/browser/0.2.0/browser-extension", browser.SideloadDirectory);
        Assert.Equal("0.1.0", browser.PreviousKnownGoodVersion);
        Assert.True(browser.ReloadRequired);

        browser.OpenBrowserSetupCommand.Execute(BrowserKind.Edge);

        Assert.Equal(BrowserKind.Edge, state.LastBrowserSetup);
        Assert.Equal("/data/packages/browser/0.2.0/browser-extension", window.ClipboardText);
        Assert.True(browser.IsBrowserSetupVisible);
        Assert.Equal("在 Edge 中完成连接", browser.BrowserSetupTitle);

        browser.Enabled = false;
        Assert.Equal(("browser", false), state.LastCollectorValue);
        Assert.Equal("已停用", browser.BrowserStatusText);
    }

    [Fact]
    public void SystemCollector_OwnsCapabilityControlsAndKeepsRequestedIntentWhenPermissionIsBlocked()
    {
        var state = new FakeDesktopState
        {
            Current = DesktopStateSnapshot.Empty with
            {
                Capabilities = new DesktopCapabilitySnapshot(new Dictionary<SystemCapability, SystemCapabilityState>
                {
                    [SystemCapability.ForegroundApp] = new(null, CapabilityAvailability.Available),
                    [SystemCapability.WindowActivity] = new(true, CapabilityAvailability.PermissionRequired, RecoveryActionAvailable: true),
                    [SystemCapability.InteractionSignal] = new(true, CapabilityAvailability.Paused),
                    [SystemCapability.InputEventRecording] = new(false, CapabilityAvailability.Available),
                })
            }
        };

        using var viewModel = TestViewModel.Create(state);

        var system = Assert.Single(viewModel.Collectors, item => item.IsSystem);
        Assert.False(system.IsExpanded);
        Assert.Collection(
            system.Capabilities,
            capability => Assert.Equal(SystemCapability.ForegroundApp, capability.Id),
            capability => Assert.Equal(SystemCapability.WindowActivity, capability.Id),
            capability => Assert.Equal(SystemCapability.InteractionSignal, capability.Id),
            capability => Assert.Equal(SystemCapability.InputEventRecording, capability.Id));

        var windowActivity = Assert.Single(
            system.Capabilities,
            capability => capability.Id == SystemCapability.WindowActivity);
        Assert.True(windowActivity.RequestedEnabled);
        Assert.Equal("需要授权", windowActivity.StatusText);
        Assert.True(windowActivity.ShowRecoveryAction);

        windowActivity.RequestedEnabled = false;
        windowActivity.RecoverCommand.Execute(null);

        Assert.Equal((SystemCapability.WindowActivity, false), state.LastSystemCapabilityValue);
        Assert.Equal(SystemCapability.WindowActivity, state.LastRecoveredSystemCapability);
    }

    [Fact]
    public void PermissionBlockedCapability_ExplainsHowToAddAndLocateHeartbeat()
    {
        var state = new FakeDesktopState
        {
            Current = DesktopStateSnapshot.Empty with
            {
                Capabilities = new DesktopCapabilitySnapshot(new Dictionary<SystemCapability, SystemCapabilityState>
                {
                    [SystemCapability.ForegroundApp] = new(null, CapabilityAvailability.Available),
                    [SystemCapability.WindowActivity] = new(
                        true,
                        CapabilityAvailability.PermissionRequired,
                        RecoveryActionAvailable: true,
                        ApplicationLocationActionAvailable: true),
                    [SystemCapability.InteractionSignal] = new(false, CapabilityAvailability.Available),
                    [SystemCapability.InputEventRecording] = new(false, CapabilityAvailability.Available),
                })
            }
        };
        using var viewModel = TestViewModel.Create(state);

        var system = Assert.Single(viewModel.Collectors, item => item.IsSystem);
        var capability = Assert.Single(
            system.Capabilities,
            item => item.Id == SystemCapability.WindowActivity);

        Assert.True(capability.HasPermissionHelp);
        Assert.Contains("左下角“+”", capability.PermissionHelpText);
        Assert.True(capability.ShowApplicationLocationAction);

        capability.RevealApplicationCommand.Execute(null);

        Assert.Equal(SystemCapability.WindowActivity, state.LastRevealedSystemCapability);
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
        Assert.False(viewModel.HasAvailableUpdate);

        updates.Publish(new UpdateSnapshot(UpdateState.ReadyToApply, "2.0.0"));

        Assert.True(viewModel.ApplyUpdateCommand.CanExecute(null));
        Assert.True(viewModel.HasAvailableUpdate);
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
                Settings = new DesktopSettingsSnapshot("old-key", "Workstation", 2),
                LoginStartEnabled = false
            }
        };
        using var viewModel = TestViewModel.Create(state);

        Assert.False(viewModel.ShowSaveBar);

        viewModel.ApiKey = " new-key ";
        viewModel.DeviceName = " Desktop ";
        viewModel.UploadIntervalMinutes = "5";
        Assert.True(viewModel.ShowSaveBar);
        viewModel.SaveConfigCommand.Execute(null);
        viewModel.LoginStartEnabled = true;

        Assert.Equal(new DesktopSettingsInput("new-key", "Desktop", 5), state.LastSettings);
        Assert.Equal(true, state.LastLoginStartValue);
        Assert.True(viewModel.ShowSaveBar);
    }

    [Fact]
    public void ThemeSelection_IsImmediateAndDoesNotCountAsAnUnsavedTextSetting()
    {
        var state = new FakeDesktopState();
        using var viewModel = TestViewModel.Create(state);

        viewModel.SelectedThemeIndex = (int)DesktopThemeMode.Dark;

        Assert.Equal(DesktopThemeMode.Dark, state.LastThemeMode);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void ImmediateStateChanges_DoNotDiscardDraftConnectionSettings()
    {
        var initial = DesktopStateSnapshot.Empty with
        {
            Settings = new DesktopSettingsSnapshot("old-key", "Desktop", 2)
        };
        var state = new FakeDesktopState { Current = initial };
        using var viewModel = TestViewModel.Create(state);
        viewModel.ApiKey = "draft-key";

        state.Publish(initial with
        {
            Settings = initial.Settings with { ThemeMode = DesktopThemeMode.Dark },
            LoginStartEnabled = true
        });

        Assert.Equal("draft-key", viewModel.ApiKey);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Equal(DesktopThemeMode.Dark, viewModel.SelectedThemeMode);
    }

    [Fact]
    public void NavigationAndDiagnostics_AreExplicitPresentationState()
    {
        using var viewModel = TestViewModel.Create();

        viewModel.NavigateCommand.Execute("Collectors");
        viewModel.ToggleDiagnosticsCommand.Execute(null);

        Assert.True(viewModel.IsCollectorsSelected);
        Assert.False(viewModel.IsOverviewSelected);
        Assert.Equal((int)MainPage.Collectors, viewModel.SelectedPageIndex);
        Assert.True(viewModel.IsDiagnosticsExpanded);

        viewModel.SelectedPageIndex = (int)MainPage.Settings;
        Assert.True(viewModel.IsSettingsSelected);
    }

    [Fact]
    public void SidebarPresentation_ExposesAnAnimatedLabelTarget()
    {
        using var viewModel = TestViewModel.Create();

        Assert.Equal(1, viewModel.SidebarLabelOpacity);

        viewModel.IsSidebarCollapsed = true;

        Assert.Equal(0, viewModel.SidebarLabelOpacity);
        Assert.Equal(2, viewModel.SidebarIconColumnSpan);
    }

    [Fact]
    public void UnsupportedPlatformUpdateOperations_AreHiddenByPresentationState()
    {
        using var viewModel = TestViewModel.Create(
            updates: new FakeUpdateController { IsSupported = false });

        Assert.False(viewModel.UpdatesSupported);
    }

    [Fact]
    public void OptionalSystemCapabilities_AreOffWithoutChangingTheForegroundBaseline()
    {
        var state = new FakeDesktopState
        {
            Current = DesktopStateSnapshot.Empty with
            {
                Capabilities = DesktopCapabilitySnapshot.MacAppOnly
            }
        };

        using var viewModel = TestViewModel.Create(state);

        var system = Assert.Single(viewModel.Collectors, item => item.IsSystem);
        var foreground = Assert.Single(system.Capabilities, item => item.Id == SystemCapability.ForegroundApp);
        var recording = Assert.Single(system.Capabilities, item => item.Id == SystemCapability.InputEventRecording);
        Assert.False(foreground.HasToggle);
        Assert.True(foreground.IsEffective);
        Assert.True(recording.HasToggle);
        Assert.False(recording.RequestedEnabled);
        Assert.Equal("已关闭", recording.StatusText);
    }

    [Fact]
    public void MacTitleDepth_IsUserControlledAndOffersPermissionRecovery()
    {
        var initial = DesktopStateSnapshot.Empty with
        {
            Capabilities = new DesktopCapabilitySnapshot(new Dictionary<SystemCapability, SystemCapabilityState>
            {
                [SystemCapability.ForegroundApp] = new(null, CapabilityAvailability.Available),
                [SystemCapability.WindowActivity] = new(false, CapabilityAvailability.Available),
                [SystemCapability.InteractionSignal] = new(false, CapabilityAvailability.Available),
                [SystemCapability.InputEventRecording] = new(false, CapabilityAvailability.Available),
            })
        };
        var state = new FakeDesktopState { Current = initial };
        using var viewModel = TestViewModel.Create(state);

        var system = Assert.Single(viewModel.Collectors, item => item.IsSystem);
        var windowActivity = Assert.Single(
            system.Capabilities,
            item => item.Id == SystemCapability.WindowActivity);
        Assert.True(windowActivity.HasToggle);
        Assert.False(windowActivity.RequestedEnabled);

        windowActivity.RequestedEnabled = true;

        Assert.Equal((SystemCapability.WindowActivity, true), state.LastSystemCapabilityValue);

        state.Publish(initial with
        {
            Capabilities = new DesktopCapabilitySnapshot(new Dictionary<SystemCapability, SystemCapabilityState>
            {
                [SystemCapability.ForegroundApp] = new(null, CapabilityAvailability.Available),
                [SystemCapability.WindowActivity] = new(true, CapabilityAvailability.PermissionRequired, true),
                [SystemCapability.InteractionSignal] = new(false, CapabilityAvailability.Available),
                [SystemCapability.InputEventRecording] = new(false, CapabilityAvailability.Available),
            })
        });

        Assert.True(windowActivity.RequestedEnabled);
        Assert.Equal("需要授权", windowActivity.StatusText);
        Assert.True(windowActivity.ShowRecoveryAction);
        windowActivity.RecoverCommand.Execute(null);
        Assert.Equal(SystemCapability.WindowActivity, state.LastRecoveredSystemCapability);
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
