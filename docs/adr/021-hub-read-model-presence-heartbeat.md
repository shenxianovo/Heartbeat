# ADR-021: hub 集面读模型与 presence 心跳通道

## Status: Accepted

## Date: 2026-07-11

## Context

### 三个观众伸手进采集器内部

`AppMonitorService`（内置 system 采集器）一个类养三个观众：折叠状态机（本职）、WPF 的 `CurrentAppChanged` 事件（托盘 UI 显示当前应用）、`StatusUploadWorker` 的 `GetCurrentApp()`（presence 心跳搭载）。WPF 侧 `MainViewModel` 经 `App.Services` service-locate **具体采集器类**——UI 换个显示需求就要动核心状态机文件，VM 也因此无法脱离运行中的 App 实例化（WPF 零测试的根源之一）。

同时 `desktop/CONTEXT.md` 已定义 **Active**（从流量推断采集器活跃）与 **Deactivate**（hub 侧黑名单），均未实现。实现 Active 需要 per-Source 状态，而 hub 是唯一看得到全部源流量的地方——hub 天然要长出一个读表面。

### presence 心跳把两个维度捆在一个节律里

心跳同时承载**活性**（设备在不在线）与**新鲜度**（正在干什么），共用一个周期节律。`StatusUploadIntervalSeconds` 是用户可配项，但没有任何真实的用户决策需要它——调大它同时损害两个维度，调小它只是更吵。另有一处刀刃平衡：服务端 IsOnline 窗口 30s（硬编码在 `DeviceStatusResponse` 里）与 agent 默认推送节律 30s **相等**，单次丢包/抖动即闪断离线。

### 备选方案与否决理由（未来评审会再提，故记录）

- **从段流量派生 current activity（hub 侧或服务端侧）**：采集器转场时只推**旧段的闭合快照**，新段要等 30s 快照节拍才首次进 hub（`BuildSegment` 的 ≥1s 噪声闸门也禁止补推零长开段快照——拆闸门会让每次 alt-tab 扫过都变成上行段）。服务端派生还要再等上传批次（≥1min）。派生值最坏滞后 ~90s，且转场后的窗口期里"最新段"指向**上一个 app**。上传/快照批次节律为效率与统计无损服务，不应绑架"现在时"的新鲜度。
- **活性从上传流量派生（删周期心跳）**：在线窗口被迫放宽到上传间隔+余量（分钟级），并把 presence 与上传批次耦合。一个傻周期心跳更简单、更诚实。
- **纯变了就推（无 keepalive）**：呆在同一个 app 里的设备显象离线。

## Decision

### 1. hub 长出集面读模型，采集器回归纯折叠

`SegmentIngestService`（hub）新增与段缓冲**分离**的读表面（不随 drain 清空）：

- **Current Activity（当前活动）**：system 采集器在转场点（前台切换、进出 away——即今天发 `CurrentAppChanged` 的同一批点位）把当前活动推给 hub。进程内、事件驱动、零延迟。away 原样暴露（`__away__`），语义解释留给消费者（ADR-014 立场）。推送走独立的单方法 seam（与 `ISegmentSink` 同构），hub 是唯一生产 adapter。
- **per-Source last-seen**：`Accept` 时刻戳，即 Active 词条的全部机制。Deactivate（黑名单/403/管理 UI）**不在本 ADR**——等第一个插件采集器落地再建，不为空气建 UI。
- hub 在 Current Activity 变化时发变更通知，消费者订阅。

`AppMonitorService` 卸下 `CurrentAppChanged` 与 `GetCurrentApp()`；`MainViewModel` 与心跳 worker 改为只消费 hub 读模型，顺带消灭 VM 对具体采集器类的 service-locate。

### 2. "正在干什么"跟着心跳走：周期＝活性，事件＝新鲜度

- 心跳 wire 形状不变（`DeviceStatusRequest.CurrentApp`），数据源改为 hub 读模型。
- **变了就推**：心跳 worker 订阅 hub 变更通知，当前活动变化时立刻补推一次心跳（新鲜度）。
- **周期 keepalive 保留**（活性），间隔从用户配置降级为**代码常量**：`StatusUploadIntervalSeconds` 从 `AgentConfig` 与 WPF 设置界面删除（旧 config.json 的字段被反序列化静默忽略，无迁移，与 ADR-020 §6 同姿态）。
- 数字：keepalive 常量 **30s**；服务端在线窗口调宽到 **90s**。规则：**窗口 ≥ 2× 心跳间隔**（取 3× 抗抖动），窗口从 DTO 里的魔数变成与此规则注释同住的显式常量。
- 前端 5s 轮询不变。current app 端到端新鲜度从 ≤~35s（周期 30s + 轮询 5s）改善为 ≤~5s+网络。

### 3. 竞态与既有立场

- 进入 away 的即推与系统挂起竞态（`Suspend` 在挂起前触发，推送可能发不出去）：可接受——keepalive 停摆后窗口自然过期、设备显象离线，与今天行为一致。
- 心跳仍**无缓存无重试**（presence 是易逝信息，下一个心跳自然覆盖，ADR-020 §5 立场不变）。

## Consequences

- ✅ 采集器回归单一职责；三个观众收敛为一个读表面；`MainViewModel` 摆脱 service-locate（WPF 可测性的前提）。
- ✅ Active 的机制（per-Source last-seen）顺手落地；将来的插件管理 UI 免费获得数据源。
- ✅ 每个节律动机单一：周期＝活性，事件＝新鲜度；删除一个无决策价值的用户配置项。
- ✅ current app 新鲜度改善一个量级；在线判定从刀刃平衡（30s/30s）变为 3× 余量。
- ⚠️ 设备真离线（断电/断网）后 Dashboard 最长 ~90s 才显示离线（今天名义 30s，但刀刃平衡下会闪断——用稳定换了一点迟钝）。
- ⚠️ 心跳频次上界从固定周期变为"周期 + 转场次数"；重度 alt-tab 会有突发小 POST（单用户自部署接受；必要时实施 debounce）。
- ⚠️ 旧 config.json 的 `StatusUploadIntervalSeconds` 字段孤儿化（静默忽略，无迁移代码）。

## References

<!-- Filled in as implementation lands -->

- `desktop/Heartbeat.Agent/Services/SegmentIngestService.cs` — hub：段缓冲 + 集面读模型（§1）
- `desktop/Heartbeat.Agent/Services/AppMonitorService.cs` — 卸下状态发布，转场点推 Current Activity（§1）
- `desktop/Heartbeat.Agent/Workers/StatusUploadWorker.cs` — 周期 keepalive + 订阅变更即推（§2）
- [`desktop/Heartbeat.Desktop.UI/ViewModels/MainViewModel.cs`](../../desktop/Heartbeat.Desktop.UI/ViewModels/MainViewModel.cs) — 共享 Avalonia presentation 消费 hub 读模型（§1）
- `shared/Heartbeat.Core/DTOs/Devices/DeviceStatusResponse.cs` — 在线窗口 30s→90s，魔数变显式常量（§2）
- Amends [ADR-020](./020-system-collector-through-hub.md) §5 —— StatusUploadWorker 数据源从具体采集器改为 hub 读模型；"presence 无缓存"立场不变
- [ADR-014](./014-away-detection-display-sleep.md) —— away 状态在读模型中原样暴露
- `desktop/CONTEXT.md` —— Active 词条机制落地；新增 Current Activity / Heartbeat 词条
