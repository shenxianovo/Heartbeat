# ActivityWatch 时间、输入与活动状态模型参考

调研日期：2026-10-07。用户已确认 `ow` 指 ActivityWatch。本文用于讨论，未修改 Heartbeat 的实现或长期模型文档。事实、静态推断和待确认建议分别记录。

## 范围与版本

本次用 `git ls-remote` 确认并读取固定提交：

- `aw-watcher-window`：`c8814e49780ec735caba0bca15982ef28d71900b`。
- `aw-watcher-afk`：`5aaab7429f7d6cc1604e3e23188dcc356d5b818c`。
- `aw-watcher-input`：`9bb5045456524b215ae11f422b80ec728c93bac7`。
- `aw-core`：`5d34ffeb47870d07baa55943cbba19cfe1fac67c`。
- `aw-client`：`f880a7111ecc1cf8fb54b4fce1af2c323c283661`。

以上是调研时各仓库 HEAD，不代表某个发布安装包的依赖组合；在线 `latest` 文档也不绑定发布版。未安装、运行 ActivityWatch，未实测权限、输入、锁屏或睡眠。

## 已核对的事实

### 时间点读数、存储区间与展示区间不同

ActivityWatch Event 使用 `timestamp + duration + data`。`currentwindow` 数据同时包含 `app`、`title`；`afkstatus` 是另一类型。heartbeat 在数据相等且新时间位于合并容差内时延长上一事件，而不是永久保存每个独立轮询点。[官方模型](https://docs.activitywatch.net/en/latest/buckets-and-events.html)、[固定合并实现](https://github.com/ActivityWatch/aw-core/blob/5d34ffeb47870d07baa55943cbba19cfe1fac67c/aw_transform/heartbeats.py#L26-L56)

Python 窗口路径每次先形成零时长读数，再提交 heartbeat。它的容差是 `max(poll_time × 1.5, poll_time + 1)`。没有后续相同读数时，事件可保持零时长；官方将补足其时长的假设留到分析阶段。[采集与容差](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/main.py#L30-L39)、[形成事件](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/main.py#L364-L385)、[零时长 FAQ](https://docs.activitywatch.net/en/latest/faq.html#some-events-have-0-duration-what-does-this-mean)

展示查询另外可使用 `flood` 填补容差内的短空白：数据不同则将不确定空白在两边中点处分开。它是查询时的推断，不是 OS 精确报告的变化时间。[flood 实现](https://github.com/ActivityWatch/aw-core/blob/5d34ffeb47870d07baa55943cbba19cfe1fac67c/aw_transform/flood.py#L11-L25)

查询 `A–B` 的两个步骤要区分：选择与范围相交的事件，以及将返回区间裁剪到范围内。固定 Python Peewee 后端执行裁剪；同提交的 memory、sqlite 后端只筛选相交事件，没有相同裁剪步骤，不能泛称所有后端行为一致。`query_bucket` 将查询的开始、结束传给 datastore。[query_bucket](https://github.com/ActivityWatch/aw-core/blob/5d34ffeb47870d07baa55943cbba19cfe1fac67c/aw_query/functions.py#L174-L187)、[Peewee 筛选与裁剪](https://github.com/ActivityWatch/aw-core/blob/5d34ffeb47870d07baa55943cbba19cfe1fac67c/aw_datastore/storages/peewee.py#L473-L490)、[memory 筛选](https://github.com/ActivityWatch/aw-core/blob/5d34ffeb47870d07baa55943cbba19cfe1fac67c/aw_datastore/storages/memory.py#L115-L128)、[sqlite 筛选](https://github.com/ActivityWatch/aw-core/blob/5d34ffeb47870d07baa55943cbba19cfe1fac67c/aw_datastore/storages/sqlite.py#L386-L410)

桌面 canonical 查询可将窗口事件与 `not-afk` 时段取交集，再统计应用和标题。配置也允许应用、标题或可听见的浏览器内容增加活动时段。因此“应用使用”并不是窗口 watcher 单独测得的基本事实，而取决于分析规则。[官方查询例子](https://docs.activitywatch.net/en/latest/examples/working-with-data.html#custom-queries)、[canonical 查询](https://github.com/ActivityWatch/aw-client/blob/f880a7111ecc1cf8fb54b4fce1af2c323c283661/aw_client/queries.py#L143-L194)、[区间取交集](https://github.com/ActivityWatch/aw-core/blob/5d34ffeb47870d07baa55943cbba19cfe1fac67c/aw_transform/filter_period_intersect.py#L68-L97)

### 官方 input watcher 保存输入统计，不保存逐条事件

`aw-watcher-input` 每 5 秒形成一份从上次运行到当前运行的区间统计，类型为 `os.hid.input`。统计非零时用零容差提交；全零时可合并。它不保存每次输入的时间戳或按键内容，无法恢复“时刻 A 发生了哪次键鼠事件”。[固定 input 循环](https://github.com/ActivityWatch/aw-watcher-input/blob/9bb5045456524b215ae11f422b80ec728c93bac7/src/aw_watcher_input/main.py#L22-L62)、[官方 README](https://github.com/ActivityWatch/aw-watcher-input/blob/9bb5045456524b215ae11f422b80ec728c93bac7/README.md)

固定 AFK HEAD 中它复用的 listener 聚合 `presses`、`clicks`、`deltaX`、`deltaY`、`scrollX`、`scrollY`。移动累计绝对轴向距离，点击只计按下，键盘只计首次按下并去重自动重复；这些 listener 行为对应所核对 AFK 提交，不能据此保证所有 input 发布包依赖完全相同。input 循环的旧 FIXME 与这个 listener 实现并不完全一致，判断以实际代码为准。[listener](https://github.com/ActivityWatch/aw-watcher-afk/blob/5aaab7429f7d6cc1604e3e23188dcc356d5b818c/aw_watcher_afk/listeners.py#L105-L201)

### AFK、无输入、锁屏与休眠不能互换

AFK watcher 的默认阈值是 180 秒，轮询为 5 秒；闲置超过阈值或锁屏均触发 `afk`。闲置触发时把区间起点回溯到最后一次输入，锁屏在闲置尚未超阈值时从检测到锁屏的时刻开始。macOS 读取 HID 闲置秒数和 session 锁屏标记。该输出是结合规则形成的 `afk/not-afk` 判断，没有分别表达睡眠和唤醒。[默认配置](https://github.com/ActivityWatch/aw-watcher-afk/blob/5aaab7429f7d6cc1604e3e23188dcc356d5b818c/aw_watcher_afk/config.py#L6-L14)、[状态机](https://github.com/ActivityWatch/aw-watcher-afk/blob/5aaab7429f7d6cc1604e3e23188dcc356d5b818c/aw_watcher_afk/afk.py#L95-L175)、[macOS 来源](https://github.com/ActivityWatch/aw-watcher-afk/blob/5aaab7429f7d6cc1604e3e23188dcc356d5b818c/aw_watcher_afk/macos.py#L9-L25)

因此以下四件事应分别命名：设备 awake/sleep；会话 locked/unlocked；输入闲置时间；按阈值、锁屏等规则判断的用户 AFK。用户看视频或阅读时可能没有键鼠输入，官方也承认这一局限。[AFK 准确性 FAQ](https://docs.activitywatch.net/en/latest/faq.html#how-accurate-is-activitywatch)

## 静态推断与限制

- 一个合并的 `app + title` 数据改变任一字段都会切开共同区间。按 app 汇总可以合计这些片段，但不等于保留了独立应用时间线；应用、标题由一个主体形成、使用两份 Schema 是 Heartbeat 自己的可选设计，不是 ActivityWatch 的原样模型。
- macOS Swift 路径变化时刷新旧状态到变化前 1 毫秒，容差使用真实两次 heartbeat 的间隔加 1 秒。本次核对路径未见 sleep/wake 保护，所以不能把 Python 固定容差的断链行为套给它。休眠导致的长空白可能被延长是静态风险推断，未实际验证。[Swift 时间处理](https://github.com/ActivityWatch/aw-watcher-window/blob/c8814e49780ec735caba0bca15982ef28d71900b/aw_watcher_window/macos.swift#L427-L474)

## 对 Heartbeat 的讨论建议（待用户确认）

- 应用前台时段与窗口标题时段使用各自的 Schema、独立时间线，由已经确认的前台状态 Observer 对形成过程负责。不要把“前台”直接改称“人正在使用”。
- `A–B` 首先是展示查询范围；按相交范围裁剪，不要为每次查询新建一种 Schema。若还要排除锁屏、休眠或闲置，另行确认使用时间的分析规则。
- “时刻 A 的键鼠事件”属于时间点事实；ActivityWatch 的区间输入统计不能替代它。需先确认只要事件类别，还是还要按下/释放、移动、滚动等具体信息，以及是否需要逐条保存。
- 设备休眠/唤醒与用户活动推断先分清含义，再决定是不是同一责任主体输出不同 Schema。共享 API 不决定 Observer 身份。
- 当前前台状态可保留本地内存或从正在形成的时段读取；不必为了展示现在状态另存每个轮询点。是否用固定 ContentId 覆盖当前值，不能由 ActivityWatch 类比直接推出，必须先核对 Heartbeat 的 观测内容 与历史引用规则。
- 持续区间应明确切换、采样容差、停止、崩溃和休眠的边界，保留未知空白，不默认延伸为使用时长。此处未选择 heartbeat 合并协议或存储方式。

仅增加本文；未运行应用、测试、构建或调整导航。
