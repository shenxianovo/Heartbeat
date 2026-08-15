# Collection

采集桌面设备的前台应用使用时长与各应用内活动，上传至服务端。作为常驻托盘或菜单栏应用运行。

## Language

**Agent**:
后台采集引擎，兼任本机 ingest hub（ADR-017）：监听窗口切换生成 system 段，接收各 Collector 经 loopback 推送的插件段，统一缓存与上传。hub 同时维护集面读模型（Current Activity + per-Source last-seen），是桌面 UI 与 Heartbeat 的唯一读表面（ADR-021）。
_Avoid_: Service, Worker（这些是 Agent 内部的实现层）

**Collector（采集器）**:
一个观测特定应用内活动并向 hub 推送 ActivitySegment 的组件（browser 扩展、vscode 插件等）。system 采集器内置于 Agent，同样经 hub 汇入（ADR-020），特例性仅剩两点：进程内直连 hub（不走 loopback）、不可停用。非内置采集器代码位于 `collection/collectors/`。
_Avoid_: 插件/Plugin（口语别名，UI 与文档统一用"采集器"；ADR-017 等历史文档中的 plugin 即此概念）

**App Hint（应用提示）**:
外部 Collector 在 loopback 摄入时可选上报的平台无关产品 slug（如 `edge`）。hub 的平台 adapter 在进入严格缓存前把它解析为本机可观测的 AppIdentity（Windows 进程或 macOS bundle）；缺失、未知或歧义时保留段但不关联 App，也不按名字猜测。`AppHint` 不进入 Analytics DTO、离线缓存或服务端事实。
_Avoid_: 让 Collector 写 `win:`/`mac:` 身份；把 App Hint 当作 App Key 或 AppIdentity

**Upload Stream（上传流）**:
泛化的出网流（ADR-020/022）：绑定一个出网源（IUploadSource），drain 一轮 = 先重传离线缓存，再取 fresh 出网——送达，或落离线缓存，否则自动重注入源（重注入不回滚更新的快照）。"批次不蒸发"是流自持的不变量。compact 为按流策略（segments 出网前压缩快照，input-events 不压缩）。segments 与 input-events 各一实例。
_Avoid_: UploadService（退役的三份同构模板）、Upload Channel（ADR-022 前的旧名，彼时退回项由调用方重注入）

**Active（采集器活跃）**:
从流量推断：某 Source 最近一段时间内向 hub POST 过即为 Active。机制为 hub 读模型的 per-Source last-seen（`Accept` 时刻戳，ADR-021）。新鲜度窗口不是魔法常量，而从采集器自报的 flush 周期派生（窗口 = 3× flushPeriodMs，容一次丢失 flush + 一次重试）；采集器未报时回落默认。无心跳协议——"活跃"回答的是"数据管道通不通"，浏览器没开时 browser 采集器显示为不活跃是诚实的。

**Collector Registry（采集器注册表）**:
hub 持久化的采集器账本（存于 config.json 的 `collectors`，与 ApiKey 等同类本机配置）：`source → {enabled, flushPeriodMs}`。**"已安装"即在注册表中**——采集器首次 `GET /v1/collectors/{source}/config` 时被 hub 自动记入（自动发现），浏览器关闭或 Agent 重启都不丢。两个写入方：采集器（注册、报 flushPeriodMs）与用户（在共享 Avalonia UI 翻 enabled）。未来采集器市场时代的"已安装"只是账本多一个来源（catalog 装的 vs 开发者自装的），账本形状不变（ADR-017 §5 推迟的 SDK/packaging）。
_Avoid_: 心跳注册、清单（manifest）

**Current Activity（当前活动）**:
集面读模型中"此刻在干什么"的条目：由 system 采集器在转场点（前台切换、进出 away）把 AppIdentityKey 推送进 hub，进程内事件驱动、零延迟；away 原样暴露为 `sys:away`，语义解释留给消费者。桌面 UI 与 Heartbeat 的唯一数据源。
_Avoid_: 从段流量派生（快照节律 + ≥1s 噪声闸门使派生值在转场后最长 30s 指向上一个 app，ADR-021 否决）

**Heartbeat（心跳）**:
presence 通道：周期 keepalive（活性，间隔为代码常量、不进配置）+ 变了就推（新鲜度），Current Activity 搭车上行。无缓存无重试（易逝信息，下一个心跳自然覆盖）。服务端在线窗口 ≥ 2× keepalive 间隔（ADR-021）。
_Avoid_: Status Upload（旧名，只描述了周期维度）

**Interaction Signal（交互信号）**:
只存在于本机内存中的最近点击信号，用于同窗标题变化的噪声门控；不持久化、不上传。它是缺少专用 Collector 时的可选 fallback，Windows 与 macOS 都由用户独立开关；UI 使用直白名称“点击辅助判断”并说明其 fallback 与本地瞬时性。它与 InputEvent Recording 可以共享底层平台 hook 或系统权限，但启用交互信号不代表允许保存输入事件。
_Avoid_: InputEvent（后者是持久化并上传的事实流）

**前台应用采集（Foreground App Collection）**:
system 采集器不可关闭的基线观测深度：记录当前前台 AppIdentity 与 away 转场。即使所有可选深度能力关闭，system 仍以该基线持续工作。
_Avoid_: 把它呈现为 system 总开关或可关闭的采集能力

**窗口活动采集（Window Activity Collection）**:
system 采集器的一项可选观测深度，统一包含 focused-window 切换与原始窗口标题观测。同窗标题变化是否切段由 Interaction Signal 的点击门控决定，不影响 AppIdentity 激活或 focused-window 切换。
_Avoid_: 把聚焦窗口与标题拆成两个用户能力开关

**采集能力（Collection Capability）**:
某个 Collector 拥有的一项用户可理解观测深度。固定基线没有开关；可选能力把用户的启用意图与实际运行状态分开，权限缺失、撤销或依赖未满足会暂停能力，但不改写用户意图。
_Avoid_: 用一个 bool 同时表示用户开关、权限与实际可用性

**Deactivate（停用采集器）**:
用户在共享桌面 UI 翻 enabled=false，双层执行。**礼貌层（采集器侧）**：采集器 `GET /v1/collectors/{source}/config` 见 `enabled:false` 主动停采（省流量）。**强制层（hub 侧）**：hub 对被停用 Source 的 `POST /v1/segments` 返回 403，段被丢弃——这是 loopback 无鉴权信任模型下唯一的准入闸门，采集器有 bug/第三方/装死时的兜底，不可省。Agent 够不着其他进程里的采集器，"停用"永远是 hub 侧行为。config 下行本版仅 `{enabled}`，设置项字段将来往响应里加（不引入 schema registry，ADR-017 §5）。采集器管理 UI 位于共享 Avalonia presentation（本机采集层事实，不进 Dashboard）。

**采集器页（Collector page）**:
共享桌面 UI 中管理采集器的页面，逐采集器展示 **Active**（管道通不通，只读）与 **enabled**（用户开关），并容纳采集器设置。可管理性**分级**：外部采集器条目带 enable 开关；system 采集器不可停用，前台应用采集作为无开关的固定基线，其他可选观测深度作为独立采集能力管理。每项能力的开关、实际状态、权限恢复动作与说明都归属该 Collector 条目，不另建脱离所有者的全局“采集能力”区块。窗口活动采集是一个用户能力，不把 focused-window 切换与原始标题拆成两个开关。条目模型 = 身份 + Active + 零或多个能力，天然容纳两类。
_Avoid_: 采集器栏、Collector panel

**Setup**:
Velopack 生成的安装器（Setup.exe），用户首次安装时下载运行。
_Avoid_: Installer

**Release**:
一次发布产物的集合，包含 Setup、完整包（.nupkg）和元数据文件（RELEASES），托管在 GitHub Releases。
_Avoid_: Build, Package

**Update**:
应用检测到新版本后，下载增量/完整包并在用户确认重启后应用的过程。生命周期为 Idle → UpdateAvailable → Downloading → ReadyToApply，下载失败退回 UpdateAvailable（携带失败原因）；**只有 ReadyToApply 的更新才允许被应用**。"检查"是瞬时动作而非状态，结果三分：UpToDate / UpdateFound / CheckFailed——检查失败 ≠ 已是最新。
_Avoid_: Upgrade, Patch; Pending Update（旧名，混淆了"发现"与"已下载"）

## Relationships

- 一个 **Release** 包含一个 **Setup** 和一个完整包
- **Agent** 在应用生命周期内持续运行，**Update** 需重启应用才能生效
- `Heartbeat.Collection.Hub` 提供纯 .NET 的 hub 运行时（loopback ingest、Collector Registry/declaration、认证客户端、段缓冲、Current Activity、Upload Stream、presence 与缓存 seam），可由桌面或无头 host 组合，不依赖桌面采集、UI、平台 API 或发布供应商
- `Heartbeat.Collector.System` 消费 App 激活、focused-window 切换、同窗标题变化与 away 等语义观察，产出 system ActivitySegment；平台 adapter 不把原生回调形状泄漏进状态机
- `Heartbeat.Desktop.Updater.Velopack` 统一承载 Windows/macOS 的 Velopack Update 生命周期（检查、下载、重试、ReadyToApply 门控与调度应用），并作为供应商依赖防火墙；platform head 只选择 Release channel，并在 updater 成功启动后停止 Agent、退出当前进程
- `Heartbeat.Desktop.Windows` 组合 Win32 观察、MachineGuid、图标、自启动、共享 Avalonia UI、托盘与 Velopack Update
- `Heartbeat.Desktop.Mac` 组合 NSWorkspace App/硬 away 观察、IOPlatformUUID、bundle 图标、App Hint、共享 Avalonia UI、菜单栏 accessory 生命周期与逐用户 login start
- `Heartbeat.Desktop.UI` 是共享 Avalonia presentation module；ViewModel 只依赖 platform-head seam，可在不创建原生窗口时测试

## Flagged ambiguities

- "安装" 既指首次 Setup 安装，也指更新后的应用替换 — 统一用 **Setup**（首次）和 **Update**（后续）区分。
- "插件" 曾与 **Collector** 混用（口语、UI、ADR-017 中的 "plugin"）— 已统一：唯一规范术语是 **Collector（采集器）**，UI 栏与文档一律用"采集器"。
