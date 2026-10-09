# ActivityWatch 采集能力参考

调研日期：2026-10-07。本文是进行中的调查笔记，不是已确认的模型或实现决定。未修改 Heartbeat 的协议、采集行为或长期文档。

后续讨论中，用户确认按形成观测的责任划分 Observer。前台状态 Observer 负责应用和标题，共用实例身份，使用两份 Schema 与独立时间线；休眠的主体边界另行确认。权威决定见 [ADR 0002](../../src/Docs/Heartbeat.Docs/content/docs/adr/0002-observer-capabilities-and-platform-clients.mdx)。下文保留调研时的备选，应用与标题合并为一份数据不属于当前决定。

## 固定源码版本

使用 `git ls-remote` 确认各仓库当时的 `master`，按这些提交读取源码：

- `aw-watcher-window`：`c8814e49780ec735caba0bca15982ef28d71900b`。
- `aw-watcher-afk`：`5aaab7429f7d6cc1604e3e23188dcc356d5b818c`。
- `aw-core`：`5d34ffeb47870d07baa55943cbba19cfe1fac67c`。

这些是调研时的分支提交，不表示某个已发布安装包包含相同行为。在线文档也可能与发布版不同。

## 已证实的事实

### 1. 应用和标题在同一份前台窗口数据中

官方 `currentwindow` 类型包含 `app` 和 `title`；`afkstatus` 是另一个事件类型。窗口 watcher 的平台适配器返回一份应用与标题数据，再形成事件。Python 循环将这份数据作为一个 heartbeat 提交，不为应用、标题分别建立 watcher。[官方数据模型](https://docs.activitywatch.net/en/latest/buckets-and-events.html#event-types)、[平台适配器](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/lib.py#L7-L81)、[形成并发送事件](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/main.py#L364-L385)

配置支持排除标题。Python 路径把标题改为 `excluded`；Swift 路径同样可以排除标题，并去除 URL。[配置文档](https://docs.activitywatch.net/en/latest/configuration.html#aw-watcher-window)、[Python 标题过滤](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/main.py#L388-L409)、[Swift 标题过滤](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L711-L721)

这只能证明 ActivityWatch 选择合并采集，不能直接证明 Heartbeat 必须采用同一个 Observer 或同一个 Schema。

### 2. macOS、Windows、Linux 共用能力入口，各有系统实现

macOS 的默认策略是 Swift，另外保留 JXA 和 AppleScript 策略。启动入口按平台、策略选择路径，Swift 作为子进程执行。[默认配置](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/config.py#L10-L16)、[启动分支](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/main.py#L176-L241)

macOS Swift 使用 `NSWorkspace.frontmostApplication` 读取应用；监听应用激活通知，通过 Accessibility 监听焦点窗口与标题变更，并每 10 秒补充轮询。应用和标题并非系统提供的一次原子读取。[应用通知与轮询](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L395-L407)、[窗口读取](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L561-L607)、[Accessibility 监听](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L748-L825)

Windows 通过 `GetForegroundWindow` 获取窗口，按窗口进程取得应用路径或名称，通过 `GetWindowText` 获取标题。应用查询失败时尝试 WMI；无前台窗口句柄则跳过本次采集。Linux 路径使用 Xlib，返回窗口 class 和 name。[Windows 系统调用](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/windows.py#L11-L75)、[Windows 与 Linux 返回值处理](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/lib.py#L7-L64)

因此，不同平台的 `app` 原始值并不天然拥有相同身份语义：macOS 可取显示名称，Windows 可取可执行文件名，Linux 可取窗口 class。[macOS 值选择](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L603-L607)、[Windows 名称](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/windows.py#L28-L35)、[Linux 数据](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/lib.py#L7-L19)

### 3. 标题失败和权限失败不是相同情况

官方 FAQ 说明 macOS 的窗口标题读取需要 Accessibility 权限；不需要 Screen Recording。默认 Swift 启动先调用 `checkAccess()`，没有权限则等待 10 秒后重试，尚未进入应用与窗口监听。因此它没有在该路径直接降级为仅采应用。已读到窗口后，如果标题属性读取没有得到字符串，源码使用空字符串继续形成包含应用的数据。[权限 FAQ](https://docs.activitywatch.net/en/latest/faq.html#what-macos-permissions-does-activitywatch-need)、[权限启动检查](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L386-L403)、[信任检查](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L829-L835)、[标题空值处理](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L603-L607)

不能从 ActivityWatch 的标题可排除配置，推导出“未授予标题权限时仍保证应用采集可用”。

### 4. 区间由 heartbeat 合并规则形成

`heartbeat_merge` 要求数据相等，且新事件开始时间不早于旧事件开始、不晚于旧事件结束加 `pulsetime`。合并后扩展至两事件的最晚结束。单次读数没有下一次相同读数时，可以保持零时长。官方 FAQ 将额外填补单次零时长事件的假设留到分析阶段。[合并实现](https://github.com/ActivityWatch/aw-core/blob/5d34ffeb47870d07baa55943cbba19cfe1fac67c/aw_transform/heartbeats.py#L26-L56)、[零时长说明](https://docs.activitywatch.net/en/latest/faq.html#some-events-have-0-duration-what-does-this-mean)

Python 窗口采集路径的 `pulsetime` 为 `max(poll_time × 1.5, poll_time + 1)`。该路径下，相同数据间的空白超过容差时不合并；标题改变也会改变整份数据，因此切开应用与标题共同记录的区间。[容差函数](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/main.py#L30-L39)、[使用容差](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/main.py#L375-L383)

**Swift 路径存在重要差异。** 数据改变时，它先将旧数据刷新到变化时刻前 1 毫秒；发送时容差取实际距离上次 heartbeat 的时间加 1 秒。该文件未见系统 sleep/wake 通知或休眠间隔上限检查。这意味着不能把 Python 固定容差的断链性质套到 Swift；从代码推断，长时间没有采样后，同样可能延长旧状态。此处是静态源码推断，没有实际触发机器休眠验证。[Swift 时间处理](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L427-L474)

### 5. AFK 不等于系统休眠或锁屏

AFK watcher 根据输入闲置时间与锁屏检测输出 `afk` / `not-afk`；输入闲置超时或锁屏进入 AFK。macOS 实现使用 CoreGraphics 的输入闲置秒数与 session 的锁屏标记。它没有在这份观测中分别输出 OS sleep/wake、locked/unlocked 两条事实。[状态机](https://github.com/ActivityWatch/aw-watcher-afk/blob/5aaab7429f7d6cc1604e3e23188dcc356d5b818c/aw_watcher_afk/afk.py#L95-L175)、[macOS 来源](https://github.com/ActivityWatch/aw-watcher-afk/blob/5aaab7429f7d6cc1604e3e23188dcc356d5b818c/aw_watcher_afk/macos.py#L1-L25)

默认 180 秒无输入即被认为 AFK，而机器此时可以仍醒着、未锁屏。官方 FAQ 也承认用户观看视频等活动可能没有鼠标键盘输入。因此 AFK 是根据规则推定的用户活动状态，不能直接替代系统睡眠事实。[默认配置说明](https://docs.activitywatch.net/en/latest/configuration.html#aw-watcher-afk)、[AFK 准确性](https://docs.activitywatch.net/en/latest/faq.html#how-accurate-is-activitywatch)

## 对 Heartbeat 的设计建议（尚未确认）

1. **能力名称跨平台，系统读取实现留在平台内部。** 当前项目可以按前台应用能力命名，而不用继续用整个 macOS 平台命名。改名不表示已经实现 Windows，也不需要先加入空的平台抽象。
2. **前台应用与前台窗口标题可以共用一次采集协调。** 两者描述同一前台上下文，标题必须知道所属应用；同一时间来源能减少后续关联误差。代码合并读取不自动决定是否共用 ObserverId 或 ObservationSchema。后续确认采用同一前台状态主体和两份 Schema，见开头的决定。
3. **若合并为一份观测，保留标题独立开关及失败表达。** 应用可读而标题未授权、不支持、为空时，如何继续形成应用观测，应由 Heartbeat 自己明确；不要照搬 ActivityWatch 整体等待权限的默认 Swift 行为。
4. **系统睡眠状态单独采集。** 它来自系统生命周期，与前台应用及标题不同。锁屏、输入闲置也不是睡眠的同义词；如果今后加入，应分别说明事实或推断规则，避免一个 `inactive` 混合表示。
5. **前台状态不直接等于用户正在使用。** 若提供使用时长，需要先定义是前台持续时段，还是排除锁屏、睡眠、闲置后的活动时长。
6. **时间段需要独立确认。** Heartbeat 当前只有单次读数 `StartAt = EndAt`。连续前台时段如何开始、结束、遇到丢采样或睡眠如何断开，属于新的观测与时间语义；不能在程序集改名时顺带采用 heartbeat 合并。
7. **不要复制 Swift 的无上限间隔延长。** 若采用采样推断区间，需要明确采样容差与未知空白；系统睡眠/停止采集形成的空白不得自动当成同一应用持续使用。这里是风险建议，不是现有 Heartbeat 协议。

## 未验证范围

- 未安装或运行 ActivityWatch，未验证实际系统权限、休眠、锁屏、标题变更。
- 未核对每个 ActivityWatch 历史版本的行为；本文只对应列出的提交和调研时官方在线文档。
- 本次调研未验证 Heartbeat 的标题实现、跨平台 Data 字段、区间规则或 Outbox 设计。主体决定以开头链接的 ADR 为准。
