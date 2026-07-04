using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Services;
using Heartbeat.Agent.Utils;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Usage;

namespace Heartbeat.Agent.Tests.Services;

public class AppMonitorServiceTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan d) => UtcNow += d;
    }

    private sealed class FakeWindowMonitor : IWindowEventMonitor
    {
        public ForegroundWindow Foreground { get; set; } = ForegroundWindow.None;
        public event Action<ForegroundWindow>? ForegroundWindowChanged;
        public ForegroundWindow GetForegroundWindow() => Foreground;
        public void Start() { }
        public void Stop() { }

        public void Switch(string? app, string? title = null)
        {
            Foreground = new ForegroundWindow(app, title);
            ForegroundWindowChanged?.Invoke(Foreground);
        }
    }

    private sealed class FakePowerMonitor : IPowerMonitor
    {
        public event Action? DisplayOff;
        public event Action? DisplayOn;
        public event Action? Suspend;
        public event Action? Resume;
        public void Start() { }
        public void Stop() { }
        public void RaiseDisplayOff() => DisplayOff?.Invoke();
        public void RaiseDisplayOn() => DisplayOn?.Invoke();
        public void RaiseSuspend() => Suspend?.Invoke();
        public void RaiseResume() => Resume?.Invoke();
    }

    /// <summary>可控的点击门控信号。默认视为"有点击"，使非门控测试行为不变。</summary>
    private sealed class FakeInputActivity : IInputActivitySignal
    {
        public bool Clicked { get; set; } = true;
        public void MarkClick() => Clicked = true;
        public bool ClickedWithin(TimeSpan window) => Clicked;
    }

    private ConfigManager NewConfig(params string[] awayNames)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"heartbeat-cfg-{Guid.NewGuid()}.json");
        _tempFiles.Add(tempPath);
        var cm = new ConfigManager(tempPath);
        if (awayNames.Length > 0)
            cm.Update(c => c.AwayProcessNames = [.. awayNames]);
        return cm;
    }

    private (AppMonitorService svc, FakeClock clock, FakeWindowMonitor win, FakePowerMonitor power, FakeInputActivity input)
        Build(string? initialApp = null, ConfigManager? config = null)
    {
        var clock = new FakeClock();
        var win = new FakeWindowMonitor { Foreground = new ForegroundWindow(initialApp, null) };
        var power = new FakePowerMonitor();
        var input = new FakeInputActivity();
        var cm = config ?? NewConfig();
        var svc = new AppMonitorService(clock, win, power, input, cm);
        svc.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (svc, clock, win, power, input);
    }

    [Fact]
    public void NormalSwitch_RecordsSegment()
    {
        var (svc, clock, win, _, _) = Build("vscode");

        clock.Advance(TimeSpan.FromSeconds(60));
        win.Switch("chrome");

        var usages = svc.GetAndClearUsages();
        Assert.Single(usages);
        Assert.Equal("vscode", usages[0].AppName);
        Assert.Equal(60, (usages[0].EndTime - usages[0].StartTime).TotalSeconds);
    }

    [Fact]
    public void DisplayOff_ClosesCurrentSegment_AtEventTime()
    {
        var (svc, clock, _, power, _) = Build("vscode");

        clock.Advance(TimeSpan.FromSeconds(30));
        power.RaiseDisplayOff();          // 封口 vscode 在 +30s

        clock.Advance(TimeSpan.FromMinutes(10)); // 息屏 10 分钟
        power.RaiseDisplayOn();           // 发出 away 段

        var usages = svc.GetAndClearUsages();
        Assert.Equal(2, usages.Count);

        var vscode = usages[0];
        Assert.Equal("vscode", vscode.AppName);
        Assert.Equal(30, (vscode.EndTime - vscode.StartTime).TotalSeconds);

        var away = usages[1];
        Assert.Equal(SyntheticApps.Away, away.AppName);
        Assert.Equal(600, (away.EndTime - away.StartTime).TotalSeconds);
        // away 段紧接 vscode 结束
        Assert.Equal(vscode.EndTime, away.StartTime);
    }

    [Fact]
    public void WhileAway_ForegroundChanges_DoNotLeak()
    {
        var (svc, clock, win, power, _) = Build("vscode");

        clock.Advance(TimeSpan.FromSeconds(10));
        power.RaiseDisplayOff();

        // away 期间前台乱跳（例如锁屏后系统切换），不应产生任何真实段
        clock.Advance(TimeSpan.FromSeconds(5));
        win.Switch("explorer");
        clock.Advance(TimeSpan.FromSeconds(5));
        win.Switch("LoginUI");

        clock.Advance(TimeSpan.FromMinutes(5));
        power.RaiseDisplayOn();

        var usages = svc.GetAndClearUsages();
        // 只应有: vscode(10s) + away，绝无 explorer/LoginUI
        Assert.Equal(2, usages.Count);
        Assert.Equal("vscode", usages[0].AppName);
        Assert.Equal(SyntheticApps.Away, usages[1].AppName);
        Assert.DoesNotContain(usages, u => u.AppName == "explorer" || u.AppName == "LoginUI");
    }

    [Fact]
    public void GetAndClearUsages_WhileAway_SnapshotsGrowingAwaySegment()
    {
        var (svc, clock, _, power, _) = Build("vscode");

        clock.Advance(TimeSpan.FromSeconds(20));
        power.RaiseDisplayOff();

        // 上传周期落在 away 期间：vscode 已在息屏时封口，进行中的 away 以快照发出（ADR-018）
        clock.Advance(TimeSpan.FromMinutes(3));
        var midAway = svc.GetAndClearUsages();
        Assert.Equal(2, midAway.Count);
        Assert.Equal("vscode", midAway[0].AppName);
        Assert.Equal(20, (midAway[0].EndTime - midAway[0].StartTime).TotalSeconds);
        var awaySnapshot = midAway[1];
        Assert.Equal(SyntheticApps.Away, awaySnapshot.AppName);
        Assert.Equal(180, (awaySnapshot.EndTime - awaySnapshot.StartTime).TotalSeconds);

        // 亮屏发终态快照：同 Id 同起点，服务端 upsert 收敛为一行
        clock.Advance(TimeSpan.FromMinutes(2));
        power.RaiseDisplayOn();
        var afterAway = svc.GetAndClearUsages();
        Assert.Single(afterAway);
        Assert.Equal(SyntheticApps.Away, afterAway[0].AppName);
        Assert.Equal(awaySnapshot.Id, afterAway[0].Id);
        Assert.Equal(awaySnapshot.StartTime, afterAway[0].StartTime);
        Assert.Equal(300, (afterAway[0].EndTime - afterAway[0].StartTime).TotalSeconds); // 3+2 分钟
    }

    [Fact]
    public void Flush_MidActivity_EmitsSnapshot_KeepingIdAndStart()
    {
        var (svc, clock, win, _, _) = Build("vscode");

        // flush 不封口不重开：发进行中段快照（ADR-018）
        clock.Advance(TimeSpan.FromSeconds(60));
        var flush1 = svc.GetAndClearUsages();
        var snapshot = Assert.Single(flush1);
        Assert.Equal("vscode", snapshot.AppName);
        Assert.Equal(60, (snapshot.EndTime - snapshot.StartTime).TotalSeconds);

        // 真实切换 → 终态快照与首个快照同 Id 同起点，覆盖全程 [0, 90]
        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("chrome");
        var flush2 = svc.GetAndClearUsages();
        var final = Assert.Single(flush2);
        Assert.Equal(snapshot.Id, final.Id);
        Assert.Equal(snapshot.StartTime, final.StartTime);
        Assert.Equal(90, (final.EndTime - final.StartTime).TotalSeconds);
    }

    [Fact]
    public void Resume_ReopensCurrentForegroundApp()
    {
        var (svc, clock, win, power, _) = Build("vscode");

        clock.Advance(TimeSpan.FromSeconds(30));
        power.RaiseSuspend();             // 睡眠，封口 vscode

        // 唤醒时前台已变成 chrome
        win.Foreground = new ForegroundWindow("chrome", null);;
        clock.Advance(TimeSpan.FromHours(2));
        power.RaiseResume();

        clock.Advance(TimeSpan.FromSeconds(45));
        win.Switch("notepad");            // 触发 chrome 段封口

        var usages = svc.GetAndClearUsages();
        // vscode(30s) + away(2h) + chrome(45s)
        Assert.Equal(3, usages.Count);
        Assert.Equal("vscode", usages[0].AppName);
        Assert.Equal(SyntheticApps.Away, usages[1].AppName);
        Assert.Equal("chrome", usages[2].AppName);
        Assert.Equal(45, (usages[2].EndTime - usages[2].StartTime).TotalSeconds);
    }

    [Fact]
    public void RepeatedEnterSignals_Ignored_SingleAwaySegment()
    {
        var (svc, clock, _, power, _) = Build("vscode");

        clock.Advance(TimeSpan.FromSeconds(10));
        power.RaiseDisplayOff();          // 进入 away @ +10s
        clock.Advance(TimeSpan.FromSeconds(5));
        power.RaiseSuspend();             // 重复进入信号，忽略
        clock.Advance(TimeSpan.FromSeconds(5));
        power.RaiseDisplayOff();          // 再次重复，忽略

        clock.Advance(TimeSpan.FromMinutes(1));
        power.RaiseDisplayOn();

        var usages = svc.GetAndClearUsages();
        Assert.Equal(2, usages.Count);
        var away = usages[1];
        Assert.Equal(SyntheticApps.Away, away.AppName);
        // away 起点应是第一个信号 (+10s)，终点是亮屏 (+10+5+5+60=80s)，时长 70s
        Assert.Equal(70, (away.EndTime - away.StartTime).TotalSeconds);
    }

    [Fact]
    public void LockApp_NormalizedToAway_DoesNotDriveStateMachine()
    {
        var (svc, clock, win, _, _) = Build("vscode", NewConfig("LockApp"));

        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("LockApp");            // 锁屏宿主成为前台 → 改名，但不进入 away 状态

        clock.Advance(TimeSpan.FromMinutes(2));
        win.Switch("vscode");             // 解锁回到 vscode

        var usages = svc.GetAndClearUsages();
        // vscode(30s) + away(2min，来自 LockApp 改名)
        Assert.Equal(2, usages.Count);
        Assert.Equal("vscode", usages[0].AppName);
        Assert.Equal(SyntheticApps.Away, usages[1].AppName);
        Assert.Equal(120, (usages[1].EndTime - usages[1].StartTime).TotalSeconds);
        // 关键：LockApp 没驱动状态机，所以紧接它的 displayOn 之类不参与；
        // 且改名后的段是普通段（可被 GetAndClearUsages 封口），证明未进 _isAway
    }

    [Fact]
    public void LockApp_WhileNotInAwayList_TreatedAsNormalApp()
    {
        // 未配置 AwayProcessNames（清空），LockApp 当普通应用
        var (svc, clock, win, _, _) = Build("vscode", NewConfig("__none__"));

        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("LockApp");
        clock.Advance(TimeSpan.FromSeconds(40));
        win.Switch("vscode");

        var usages = svc.GetAndClearUsages();
        Assert.Equal(2, usages.Count);
        Assert.Equal("vscode", usages[0].AppName);
        Assert.Equal("LockApp", usages[1].AppName);  // 未归一化
    }

    [Fact]
    public void SubSecondSegment_NotRecorded()
    {
        var (svc, clock, win, _, _) = Build("vscode");

        clock.Advance(TimeSpan.FromMilliseconds(500)); // <1s
        win.Switch("chrome");

        var usages = svc.GetAndClearUsages();
        Assert.DoesNotContain(usages, u => u.AppName == "vscode"); // 太短不记
    }

    [Fact]
    public void GetCurrentApp_WhileAway_ReturnsNull()
    {
        var (svc, clock, _, power, _) = Build("vscode");

        Assert.Equal("vscode", svc.GetCurrentApp());

        clock.Advance(TimeSpan.FromSeconds(10));
        power.RaiseDisplayOff();
        Assert.Null(svc.GetCurrentApp());

        clock.Advance(TimeSpan.FromMinutes(1));
        power.RaiseDisplayOn();
        Assert.Equal("vscode", svc.GetCurrentApp());
    }

    [Fact]
    public void TitleChange_SameApp_TriggersSplit()
    {
        var (svc, clock, win, _, _) = Build();
        win.Switch("msedge", "YouTube");

        clock.Advance(TimeSpan.FromSeconds(60));
        win.Switch("msedge", "GitHub"); // 同 app 不同标题 → 切段

        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("vscode", "proj");

        var usages = svc.GetAndClearUsages();
        Assert.Equal(2, usages.Count);
        Assert.Equal("msedge", usages[0].AppName);
        Assert.Equal("YouTube", usages[0].Title);
        Assert.Equal("msedge", usages[1].AppName);
        Assert.Equal("GitHub", usages[1].Title);
    }

    [Fact]
    public void SameApp_SameTitle_DoesNotSplit()
    {
        var (svc, clock, win, _, _) = Build();
        win.Switch("msedge", "YouTube");

        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("msedge", "YouTube"); // 完全相同 → 不切段

        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("vscode", null);

        var usages = svc.GetAndClearUsages();
        Assert.Single(usages);
        Assert.Equal("msedge", usages[0].AppName);
        Assert.Equal("YouTube", usages[0].Title);
        Assert.Equal(60, (usages[0].EndTime - usages[0].StartTime).TotalSeconds);
    }

    [Fact]
    public void Title_CarriedIntoSegment_AwaySegmentHasNullTitle()
    {
        var (svc, clock, win, power, _) = Build();
        win.Switch("vscode", "main.cs");

        clock.Advance(TimeSpan.FromSeconds(30));
        power.RaiseDisplayOff();
        clock.Advance(TimeSpan.FromMinutes(5));
        power.RaiseDisplayOn();

        var usages = svc.GetAndClearUsages();
        Assert.Equal(2, usages.Count);
        Assert.Equal("main.cs", usages[0].Title);           // 真实段带标题
        Assert.Equal(SyntheticApps.Away, usages[1].AppName);
        Assert.Null(usages[1].Title);                        // away 段标题为 null
    }

    [Fact]
    public void AwayProcessNames_HotReload_TakesEffectAfterConfigChange()
    {
        // 启动时 AwayProcessNames 为空 → LockApp 视为普通应用
        var clock = new FakeClock();
        var win = new FakeWindowMonitor { Foreground = new ForegroundWindow("vscode", null) };
        var power = new FakePowerMonitor();
        var cm = NewConfig("__none__"); // 不含 LockApp
        var svc = new AppMonitorService(clock, win, power, new FakeInputActivity(), cm);
        svc.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("LockApp");                 // 此时未配置 → 当普通 app
        clock.Advance(TimeSpan.FromSeconds(30));

        // 运行中更新配置：把 LockApp 加入 AwayProcessNames，应触发快照热刷新
        cm.Update(c => c.AwayProcessNames = ["LockApp"]);

        win.Switch("vscode");                  // 收尾封口 LockApp 段
        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("chrome");                  // 封口 vscode 段
        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("LockApp");                 // 配置已生效 → 应归一化为 away
        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("vscode");

        var usages = svc.GetAndClearUsages();
        // 第一个 LockApp 段(配置生效前)保留原名；第二个被归一化为 away
        Assert.Contains(usages, u => u.AppName == "LockApp");
        Assert.Contains(usages, u => u.AppName == SyntheticApps.Away);
    }

    [Fact]
    public void TitleChange_WithoutClick_DoesNotSplit()
    {
        // 同 app 标题变化、但门控无点击 → 视为程序自身抖动，不切段（ADR-016）
        var (svc, clock, win, _, input) = Build();
        input.Clicked = false;
        win.Switch("WindowsTerminal", "✳ Claude Code");

        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("WindowsTerminal", "⠐ Claude Code"); // spinner 抖动，无点击

        clock.Advance(TimeSpan.FromSeconds(30));
        input.Clicked = true;
        win.Switch("vscode", "main.cs");                // app 变，封口

        var usages = svc.GetAndClearUsages();
        // 只应有一段 WindowsTerminal（spinner 抖动没切段），标题定格在起始值：
        // 抖动值不参与归因（ADR-018，同 Id 快照身份不变）
        var terminalSegs = usages.Where(u => u.AppName == "WindowsTerminal").ToList();
        Assert.Single(terminalSegs);
        Assert.Equal("✳ Claude Code", terminalSegs[0].Title);
        Assert.Equal(60, (terminalSegs[0].EndTime - terminalSegs[0].StartTime).TotalSeconds);
    }

    [Fact]
    public void TitleChange_WithClick_Splits()
    {
        // 同 app 标题变化、门控有点击 → 人为切 tab，切段（ADR-016）
        var (svc, clock, win, _, input) = Build();
        input.Clicked = true;
        win.Switch("msedge", "YouTube");

        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("msedge", "GitHub");                 // 有点击 → 切段

        clock.Advance(TimeSpan.FromSeconds(30));
        win.Switch("vscode", "x");

        var usages = svc.GetAndClearUsages();
        var edgeSegs = usages.Where(u => u.AppName == "msedge").ToList();
        Assert.Equal(2, edgeSegs.Count);
        Assert.Equal("YouTube", edgeSegs[0].Title);
        Assert.Equal("GitHub", edgeSegs[1].Title);
    }
}
