# ADR-0007：前台应用与前台窗口是两个观测对象

## 状态：已接受

## 日期：2026-09-16

## 背景

重放页面上，同一个应用会被切成大量首尾相接的小块。追查后发现原因都在采集端，而不在展示端：

- 前台应用 Record 的判等把窗口标题一起算进去，于是同一个应用里换个文件名就结束旧 Record、另开一条。
- 应用身份带着进程 ID，重启同一个应用会得到「看起来一样但身份不同」的 Record。
- macOS 的应用激活、焦点窗口变化、标题变化通知被当成「必须切段」的信号，即使读回来的应用没变也切。
- 窗口标题读不到被当作 capability failure，直接断开前台应用区间；AX observer 刚订阅、握手还没完成的那一瞬也被报成 `WindowTitle Unavailable`，而这恰好发生在每次切换应用时。
- 采样确认链路会自我失效：`Capture()` 期间自己发布的 capability 事件也算「状态变化」，于是快照被丢弃，确认间隔被拉长到超过上限，判成观察中断。
- 默认确认上限是采样间隔的两倍，一次晚到的 tick 就够判定断采。

`main` 的旧实现没有这么碎，是因为它给标题变化加了「最近 1 秒内有点击」的门控（`AppMonitorService.TitleGateWindow`），并且没有把标题读取失败连坐到应用区间。但点击门控是拿输入活动去猜「这次标题变化算不算换事情」，本身是个启发式：没点击就漏切，点了就切，解释不了也测不准。

考虑过三种做法：展示端合并相邻同应用块（把采集端的错误留在数据里，读侧每个消费者都要重复补齐）；照搬 main 的点击门控（把启发式重新引进来）；把窗口标题从应用观测里拆出去。

## 决策

前台应用与前台窗口是两个观测对象，各自成一条 Track：`desktop.application.foreground` v1 只回答「前台是哪个应用」，新增的 `desktop.window.foreground` v1 只回答「前台窗口叫什么」。

由此确定四条规则：

1. 一个观测对象在读数不变且确认没有中断的整段时间里只有一条 Record。原生通知只是触发重新读数，不是切段信号；读回来的值没变就继续延长同一条 Record。所以同一个应用不会出现两条首尾相接的 Record，展示端不需要合并相邻块。
2. 应用身份只由平台、标识种类和原生标识构成，不含进程 ID。进程 ID 是同一个应用的运行时细节。
3. 能力失败只断开它自己那个观测：`window_title` 失败只结束窗口 Record，`application` 失败才同时结束应用与窗口。能力状态要如实描述——观察器订阅已发起但握手未完成时，既不声称可用，也不宣布中断。
4. 确认链路只对「前台状态是否可能已经变化」敏感。capability 与 input 事件不使快照失效；因活动变化被丢弃的采样立刻重排，不等下一个周期。默认确认上限放宽到采样间隔三倍，连续两次缺失确认才判定观察中断。

不恢复 main 的点击门控。标题变化就是窗口观测的变化，不需要输入活动来批准。

## 后果

- ✅ 同一个应用在整段前台时间里是一条 Record，断开只发生在真的有 Away Signal、`application` 能力失败或确认中断时，「断采」从此是一个有原因的事实而不是噪声。
- ✅ 标题这类高频、易失败、需要额外权限的读数被隔离在自己的 Track 上，权限缺失时降级范围与失败原因都能对上。
- ✅ 不再需要点击门控这类启发式，采集端的切段规则可以逐条写成测试。
- ⚠️ 消费者要读两条 Track 才能同时得到「哪个应用 + 什么窗口」，展示上应用与窗口是两条泳道。
- ⚠️ 窗口标题常含文件名、聊天对象、网页标题。它现在是一条独立 Track，隐私取舍要落在读侧的可见性与过滤上，采集端不做静默丢弃。
- ⚠️ 应用身份不含进程 ID，因此同一个应用的两次运行在 Record 上无法区分；确实需要区分进程时要另设观测，不能靠这条协议。

## 参考

- [`docs/protocols/desktop-application-foreground-v1.md`](../protocols/desktop-application-foreground-v1.md) — 前台应用协议。
- [`docs/protocols/desktop-window-foreground-v1.md`](../protocols/desktop-window-foreground-v1.md) — 前台窗口协议。
- [`src/Collectors/Heartbeat.Collector.Desktop.Mac/DesktopRecordProjector.cs`](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/DesktopRecordProjector.cs) — 两个观测对象的区间投影。
- [`src/Collectors/Heartbeat.Collector.Desktop.Mac/MacSystemObservationSource.cs`](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/MacSystemObservationSource.cs) — 能力状态与标题读取。
- [`ADR-0005`](ADR-0005-minimal-macos-desktop-collector.md) — 最小 macOS Collector 的起点范围。
- [领域语言](../../CONTEXT.md) — Foreground Application Observation 与 Foreground Window Observation。
