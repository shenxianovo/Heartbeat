# Collection

采集 Windows 前台窗口使用时长与各应用内活动，上传至服务端。作为常驻托盘应用运行。

## Language

**Agent**:
后台采集引擎，兼任本机 ingest hub（ADR-017）：监听窗口切换生成 system 段，接收各 Collector 经 loopback 推送的插件段，统一缓存与上传。hub 同时维护集面读模型（Current Activity + per-Source last-seen），是 WPF 与 Heartbeat 的唯一读表面（ADR-021）。
_Avoid_: Service, Worker（这些是 Agent 内部的实现层）

**Collector（采集器）**:
一个观测特定应用内活动并向 hub 推送 ActivitySegment 的组件（browser 扩展、vscode 插件等）。system 采集器内置于 Agent，同样经 hub 汇入（ADR-020），特例性仅剩两点：进程内直连 hub（不走 loopback）、不可停用。插件采集器代码位于顶层 `collectors/`。

**Upload Stream（上传流）**:
泛化的出网流（ADR-020/022）：绑定一个出网源（IUploadSource），drain 一轮 = 先重传离线缓存，再取 fresh 出网——送达，或落离线缓存，否则自动重注入源（重注入不回滚更新的快照）。"批次不蒸发"是流自持的不变量。compact 为按流策略（segments 出网前压缩快照，input-events 不压缩）。segments 与 input-events 各一实例。
_Avoid_: UploadService（退役的三份同构模板）、Upload Channel（ADR-022 前的旧名，彼时退回项由调用方重注入）

**Active（采集器活跃）**:
从流量推断：某 Source 最近一段时间内向 hub POST 过即为 Active。机制为 hub 读模型的 per-Source last-seen（`Accept` 时刻戳，ADR-021）。无注册表、无心跳协议——"活跃"回答的是"数据管道通不通"，浏览器没开时 browser 采集器显示为不活跃是诚实的。

**Current Activity（当前活动）**:
集面读模型中"此刻在干什么"的条目：由 system 采集器在转场点（前台切换、进出 away）推送进 hub，进程内事件驱动、零延迟；away 原样暴露（`__away__`），语义解释留给消费者。WPF 当前应用显示与 Heartbeat 的唯一数据源。
_Avoid_: 从段流量派生（快照节律 + ≥1s 噪声闸门使派生值在转场后最长 30s 指向上一个 app，ADR-021 否决）

**Heartbeat（心跳）**:
presence 通道：周期 keepalive（活性，间隔为代码常量、不进配置）+ 变了就推（新鲜度），Current Activity 搭车上行。无缓存无重试（易逝信息，下一个心跳自然覆盖）。服务端在线窗口 ≥ 2× keepalive 间隔（ADR-021）。
_Avoid_: Status Upload（旧名，只描述了周期维度）

**Deactivate（停用采集器）**:
hub 端拒收：被停用的 Source 在 hub 的本地黑名单中，其 POST 返回 403，段被丢弃。Agent 够不着其他进程里的插件，"停用"永远是 hub 侧行为；插件收到 403 应退避并定期重试（与 hub 未启动时的退避是同一逻辑）。插件管理 UI 位于 WPF（本机采集层事实，不进 Dashboard）。

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

## Flagged ambiguities

- "安装" 既指首次 Setup 安装，也指更新后的应用替换 — 统一用 **Setup**（首次）和 **Update**（后续）区分。
