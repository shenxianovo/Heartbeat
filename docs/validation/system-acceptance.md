# system 恢复验收与修复

日期：2026-09-14。审查基线 `0bcd3b0a094b7478f7fa39b178f316b7f87b664c`，对象为 Sol 工作树中的全部未提交变更和新增文件。不是仅审查提交历史。

代码审查及自动回归已完成，确认的问题已修复。真实 macOS Collector 到 Hub 持久接管冒烟通过；完整平台交互和采集到 Web 的真机验收仍未完成，不能据自动测试宣布所有系统能力验收通过。

## 已确认范围

本轮恢复 macOS 应用、窗口/标题、四类独立 Away Signal、非文本物理输入与历史能力状态；共用内存缓冲及 Hub 交接；统一时间轴组合来源，查询时对已存 Point Record 分桶，局部原始明细分页读取。未知协议仍可查看时间位置与 JSON。Windows 只盘点，见 [能力对照](system-capability-inventory.md)。

Hub 前允许丢失，不引入持久队列或容量治理。结果更正只接受了 [ADR-0006](../adr/ADR-0006-result-correction-semantics.md) 的模型语义，本轮没有实现修改历史 Record Value 的机制。默认最大应用确认间隔为采样间隔三倍，是可配置的临时规则；取三倍是为了让连续两次缺失确认才判定观察中断，一次晚到的 tick 算正常调度抖动。

## Standards

此轴检查仓库已确认职责、并发边界与维护性；协议分支重复属于维护性判断，不以代码量定性架构腐化。

| 原发现 | 影响 | 修复及证据 |
| --- | --- | --- |
| P1：事件与采样并发修改投影，事件在出队时才取时间 | 旧快照覆盖较新的事件；采样阻塞后事件时间延后 | [Session](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/DesktopCollectorSession.cs) 在接收时取时间，用单消费者处理投影；采样期间发生状态变化则丢弃快照。启动及周期采样阻塞回归均先失败后通过 |
| P2：批量准备在 Stage 共用锁中重复序列化增长中的数组 | 高频输入使锁占用及分配量增长，违背轻量、非阻塞交接目的 | [PendingHubSubmissions](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/PendingHubSubmissions.cs) 锁内仅复制快照，锁外单次计量每条记录；500 条小记录分配量实测从 38,100,600 降至 124,928 字节。保留精确字节边界与旧回执回归 |
| P2：概览与详情各自按协议分支和解析 | 增加专用展示需要协调两套注册，存在重复修改风险 | [registry](../../src/Frontend/Heartbeat.Web/src/components/records/renderers/registry.ts) 同时注册详情组件和概览摘要，两者共用协议解析；公共时间线不再识别业务 JSON |

3 项已修复；此轴最严重的问题为 P1 投影并发与事件时间错误。

## Spec

此轴按已确认行为检查实现；事件串行化问题与 Standards 重叠，保留两个独立判断。

| 原发现 | 对应要求及影响 | 修复及证据 |
| --- | --- | --- |
| P1：接收事件被采样延后或过期快照覆盖 | 按真实观察形成时间区间 | 同上，测试验证采样结束前收到的事件保留接收时间和转场归属 |
| P1：macOS uptime 时间不包含系统休眠 | 休眠及恢复必须落在正确时间；旧实现唤醒后会持续落后 | [MacContinuousTimeProvider](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/MacContinuousTimeProvider.cs) 使用包含休眠的 `CLOCK_MONOTONIC_RAW`。模拟一小时休眠、唤醒前输入及正反改钟；回改为 uptime 时测试再次失败；真实原生时钟读取通过 |
| P2：未订阅/翻译 `flagsChanged` | 恢复去重物理 key-down；修饰键原先漏采 | 补齐事件 mask、原生 flags 和左右键物理位映射。CapsLock 仅解释 stateless 物理位，不误用锁定状态；对应映射回归通过 |
| P2：AX 错误被当作空标题，订阅重试即报告恢复 | 能力故障应独立降级，并保留真实历史状态 | 正常无值与失败分开，实际附着且读取成功才报告 available；连续读取失败与未附着观察器回归通过 |
| P2：一秒区间被强制扩成全天视图的 0.35% | 展示不得虚构连续性或填补观测空白 | [TimelineViewport](../../src/Frontend/Heartbeat.Web/src/components/replay/TimelineViewport.tsx) 按实际时间比例绘制；窄记录仅保留像素级可见标记，不再将一秒扩成约五分钟 |
| P2：同 Track 重叠区间相互遮挡 | 重叠 Away 原因与各能力历史状态都应可见 | [rangeLayout](../../src/Frontend/Heartbeat.Web/src/components/replay/rangeLayout.ts) 仅按时间安排多行；浏览器测试检查几何不重叠，普通点击可展开两条记录 |
| P2：切换来源后继续显示旧记录详情 | 共用筛选应控制概览及明细 | 选择只保存身份，详情从当前数据派生；切走来源后旧详情消失，组件回归通过 |
| P2：来源只能全选或单选，空选择无限加载 | 统一时间轴允许组合不同来源 | 来源多选和“选择全部来源”，空选择明确提示；组合选择和空选择浏览器回归通过 |

8 项已修复；此轴最严重的问题为 P1 事件时间与休眠后时间错误。

## 架构结论与接入阻力

当前没有证据说明已经整体重现 main 的职责混杂，但这轮确实发现了具体的接入阻力：高频提交准备成本、采样与事件并发、展示协议重复分支。修复集中在这些边界，没有搬回旧 Runtime 或建立新的通用采集框架。

- 新增业务载荷仍由 Collector 定义；Hub、后端存储及 [Point 计数查询](../../src/Backend/Heartbeat.Infrastructure/Persistence/PostgresPointRecordCountStore.cs) 只使用公共归属、身份、时间字段。Point 计数表达已存 Record 数量，不能自动等同任意业务统计。
- 新 Track 的基础时间/JSON 可见性不要求增加 renderer；需要专用概览和详情时，在同一注册入口扩展。时间布局、筛选和分页复用公共实现。
- 历史 Value 更正仍受当前 Hub/后端不变量限制，这是明确保留的实现缺口；任意 JSON 解决载荷形状，不能代替结果更正语义。
- 当前先完整加载 Range、Point 按窗口计数及分页读明细；大量 Range 下的浏览器布局与内存仍未做容量压测。内存事件队列和待交接缓冲也没有容量保证，符合本轮暂缓约束。

## 验证

所有确认的行为缺陷均补最小失败复现后修复。旧测试主要覆盖独立投影和顺序输入，缺少阻塞采样时序、休眠与 uptime 的差别、修饰键原生事件及失败 AX 读取；旧浏览器验证也没有断言重叠区间的可点击几何和任意来源组合。这些边界已补入回归。

| 检查 | 结果 |
| --- | --- |
| `dotnet test Heartbeat.slnx --no-restore --verbosity minimal` | 全绿，零失败零跳过；领域、desktop、Hub 与真实 PostgreSQL 集成四层都在内 |
| Web `npm run verify` | 类型检查、ESLint、Prettier、Vitest、生产构建全部通过 |
| Web `npm run test:e2e` | Chromium 用例全绿；认证和 API 使用 fixture，不等于真实采集到页面联调 |
| 真实 macOS Collector → 临时 Hub | 1 秒间隔常驻 8 秒，接管 1 条持续 7.005443 秒的应用 Record、3 条能力状态；未出现交接错误，Collector 退出码 0 |
| Hub 重启 | 临时 SQLite 中 Record 数量、ID 和内容保持一致；此处是正常进程重启验证 |

各套测试的分层口径与每次运行的实际数量见[工程验证](../verification.md)，这份记录不保存会过期的数量快照。

原生冒烟使用独立 loopback Hub、随机测试 Owner/密钥和临时 SQLite；认证与后端设为不可达本地端口，不访问正式服务。只输出记录数量、区间长度与能力状态，不输出窗口标题；临时进程、日志和数据库均已清理。此次机器报告 application、window_title、input available，这只证明当次观察器状态，不证明输入完整。

## 尚未完成的真机步骤

使用 [Collector 运行方式](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md) 连接隔离的 Hub/后端，在 Web 读回同一时间窗，逐项核对：

1. 应用切换、同应用窗口/标题变化，不点击也应分段；未知空白不能自动续接。
2. 普通键长按与释放、左右修饰键分别及同时按下、CapsLock、鼠标各按钮与双向滚动；只保存去重物理按下和原生滚动，无文本。CapsLock 是否提供 stateless 位取决于真实原生事件；当前物理位置表未定义 Fn。
3. 锁屏/解锁、显示器休眠、系统休眠及重叠原因恢复；一个原因解除不能关闭其他原因，唤醒后时间位置应正确。
4. Accessibility/Input Monitoring 分别撤销和授予，其他能力继续工作，失败及恢复历史与实际行为一致。

本轮未触发这些完整平台交互，也未完成它们到 Web 的真实链路。测试已通过的范围与这些待验项应分开报告。
