# ADR-026: 采集器注册表与双层停用

## Status: Proposed

## Date: 2026-07-16

## Context

ADR-017 把 Agent 变为本机 ingest hub、采集器经 loopback 汇入，但只解决了**上行**（段推入）。hub 对采集器的认知止于"谁最近推过段"（`SourceLastSeen`，ADR-021）——这回答"管道通不通"（Active），却答不出两个 WPF 采集器栏必须回答的问题：

- **谁装了但没在跑？** 关掉浏览器后 browser 采集器不再推段，hub 无从区分"从没装过"与"装了但此刻没开"。Active 是流量事实，不是安装事实。
- **怎么停用一个采集器？** CONTEXT.md 的 Deactivate 词条描述了"hub 黑名单 + 403 拒收"，但全仓库 grep 不到任何 403/黑名单代码——是纯文档、未落地。要做真开关就得从零写。

同时 Active 的新鲜度窗口无处安放：判定"多久没推算不活跃"需要一个阈值，而这个阈值本质由采集器的 flush 周期决定（browser 受 Chrome MV3 `chrome.alarms` 下限约束固定为 30s）。写死一个 hub 侧魔法常量，会与采集器脱节，且第二个节律不同的采集器出现时即失真。

更远处是采集器市场愿景（官方 catalog + 开发者自写采集器，ADR-017 §5 已显式推迟 SDK/packaging）。本 ADR 不建市场，但要确保现在立的地基不挡它。

## Decision

### 1. 采集器注册表：hub 持久化的"已安装"账本

新增 `AgentConfig.Collectors: Dictionary<source, {Enabled, FlushPeriodMs}>`，落于 config.json，复用 `ConfigManager` 的原子写 + `ConfigChanged` + `Clone`/`Normalize`（与 `AwayProcessNames` 同一模式）。

**"已安装"即在注册表中。** 采集器首次触达 hub 时被自动记入——这就是自动发现，config.json 因"第一次见到某采集器"而长出条目是预期行为，不再是纯用户配置文件。两个写入方：采集器（注册、报 FlushPeriodMs）与用户（翻 Enabled，经 WPF）；`ConfigManager` 的锁保证并发安全，正是多写入方场景所需，故不另起独立文件。

字典按 `source` 名作 key，留 `Enabled`/`FlushPeriodMs` 之外的扩展位——市场时代的"已安装"只是账本多一个来源（catalog 装 vs 自装），账本形状不变。

### 2. 配置下行协议：采集器拉，hub 存

新增 `GET /v1/collectors/{source}/config` → `{"enabled": bool}`。方向是**采集器发起**——MV3 扩展不能当服务器，hub 永远无法主动打进采集器进程，"双向"实为上行 POST + 下行 GET 两个由采集器发起的调用。采集器首次 GET 即触发注册（§1）。请求携带 `flushPeriodMs`（browser=30000），hub 存入注册表。

本版响应仅 `{enabled}`；采集器自身设置项字段将来往响应体里加即可，**不引入配置 schema registry**（ADR-017 §5 推迟项，当前仅一个采集器、且它已有 options.html，通用 schema 渲染是过度设计）。

### 3. Active 窗口从采集器自报节律派生

`now - SourceLastSeen[source] < 3 × FlushPeriodMs`（browser 即 90s，容一次丢失 flush + 一次 backoff 重试）。采集器未报 FlushPeriodMs 时回落默认常量。消除了 hub 侧与采集器脱节的魔法数——flush 周期是采集器固有属性（平台约束），由采集器声明、hub 读取，而非 hub 下配。

### 4. 双层停用（Deactivate）

用户在 WPF 翻 `Enabled=false`，两层正交执行：

- **礼貌层（采集器侧）**：采集器 GET config 见 `enabled:false` 主动停采。纯优化——省掉注定被拒的无效 POST 与 backoff 噪声。
- **强制层（hub 侧）**：hub 对被停用 Source 的 `POST /v1/segments` 返回 **403**，段丢弃。判定加在 `SegmentIngestRequestHandler`（与现有"拒 system→400"同层，同属传输信任守卫）。**不可省**：loopback 无鉴权，采集器可能第三方/有 bug/装死，403 是唯一由 Agent 兜底的段准入闸门；只靠采集器自觉则停用形同虚设。

采集器被 403 后的退避行为（稳定拒绝 vs 暂时故障）本版沿用现有 backoff，优化留后。

### 5. 采集器栏可管理性分级

WPF 左侧导航新增采集器栏，条目模型 = 身份 + Active（只读）+ 零或多个控件：

- **plugin 采集器**：带 enable 开关。
- **system 采集器**：只读条目，**无开关**（不可停用是其本质，置灰一个永点不动的开关是噪音），恒 Active（Agent 自身在跑，进程内直连不走 loopback、不上报 FlushPeriodMs）。它占一个位置，为将来的采集粒度控件（如 InputEvent 记录 vs 只计数，对应 CONTEXT-MAP"采集能力分层可拆"）预留其家。

system 在数据模型上是 Collector 之一（ADR-017），但"模型统一"与"管理 UI 显示什么"是两回事——分级让一栏同时容纳可开关与只读两类，无需特例分支。

## Consequences

- ✅ "已安装"有了诚实定义（在注册表 = 被 hub 见过），与"Active"（管道通不通）正交，采集器栏两个状态各有其源。
- ✅ Deactivate 从纯文档落为双层实现；强制层 403 是无鉴权 loopback 下唯一真闸门。
- ✅ Active 窗口无魔法数——由采集器自报 flush 周期派生，第二个采集器接入即自适应。
- ✅ 注册表复用 `ConfigManager` 全套（原子写/通知/并发），零新持久化机制；字典形状为市场愿景留门。
- ⚠️ config.json 由纯用户配置变为"用户配置 + 机器自动发现数据"混居；自动增长条目是预期，但读文件者需知其两重来源。
- ⚠️ browser 采集器与 hub 必须同版一起改（新增 GET config 调用 + 上报 flushPeriodMs + 尊重 enabled）——注册协议本就两侧契约。
- ⚠️ 403 后采集器沿用为"hub 不在"设计的 backoff（最长 10min 重试），面对稳定拒绝略有无效轮询；可接受，优化留后。
- ⚠️ source 名当前无命名空间，市场时代需防碰撞（两个开发者都叫 `browser`）——本版不解决，字典 key 结构不挡后续加 `origin`/`version`。

## References

- [`desktop/CONTEXT.md`](../../desktop/CONTEXT.md) — Active / Collector Registry / Deactivate / 采集器栏 词条
- [`desktop/Heartbeat.Agent/Configuration/ConfigManager.cs`](../../desktop/Heartbeat.Agent/Configuration/ConfigManager.cs) — 注册表持久化载体（§1）
- [`desktop/Heartbeat.Agent/Services/SegmentIngestRequestHandler.cs`](../../desktop/Heartbeat.Agent/Services/SegmentIngestRequestHandler.cs) — GET config 路由 + 403 强制层（§2、§4）
- [`desktop/Heartbeat.Agent/Services/ICollectionStatus.cs`](../../desktop/Heartbeat.Agent/Services/ICollectionStatus.cs) — SourceLastSeen，Active 数据源（§3）
- [`collectors/browser/src/background.ts`](../../collectors/browser/src/background.ts) — flush 周期 30s（§3），需新增 GET config + enabled 尊重（§4）
- [`desktop/Heartbeat.WPF/MainWindow.xaml`](../../desktop/Heartbeat.WPF/MainWindow.xaml) — 采集器栏 UI（§5）
- [`desktop/Heartbeat.WPF/ViewModels/MainViewModel.cs`](../../desktop/Heartbeat.WPF/ViewModels/MainViewModel.cs) — 采集器栏 VM（§5）
- Builds on [ADR-017](./017-activity-segment-pluggable-collectors.md) — pluggable collectors；§5 推迟的 SDK/packaging 即此处的市场愿景
- Builds on [ADR-021](./021-hub-read-model-presence-heartbeat.md) — SourceLastSeen 读模型；Active 机制
