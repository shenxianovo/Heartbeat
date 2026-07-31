# ADR-032: Device 即观测主体与多实例采集拓扑

## Status: Accepted

## Date: 2026-07-31

> 设计定于 2026-07-31 grilling session。本 ADR 只落文档语义与命名纪律，
> **零 schema / 零代码改动**；无头 hub 与 vrchat 采集器的实现另行动工。

## Context

Quest 3 上的 VRChat 在线状态无法在设备本机采集（装不了客户端，装了也没权限），
只能经 VRChat 云端 API 从外部观测。这暴露出模型里一个从未言明的假设——"采集器
观测它所在的机器"。核查 ADR-017 的采集器契约与 ADR-030 的声明通道：**没有任何
不变量要求这一点**。采集器的义务只是声明 source + 深度表、产段、经 hub 上传；
观测点只需在信号可见的地方。

讨论中三条路线被审视：

1. **嵌套 hub**：设备侧 hub → 服务端"中心 hub" → ingest；
2. **五概念通用本体**：Owner / Subject / Source / ObservedDevice / CollectorHost，
   `Segment.DeviceId` 改可空，允许段级手动设备标注；
3. **Device 语义松动为观测主体**：账号采集器如实以账号为主体上报。

一个关键事实约束了选择：VRChat API 只能证明**账号**在线，证明不了在 Quest 还是
PC 上玩（用户两边都玩）。把账号段挂在名为 "Quest 3" 的 Device 下是制造假事实。

## Decision

### 1. Device = 观测主体

Device 从"一台采集来源机器"松动为"**一个观测主体**"——某采集器如实观测的对象：
一台机器（桌面 Agent 观测本机），或一个账号（vrchat.account 采集器观测 VRChat
账号）。当前全部 Device 行都是机器，故 schema（OwnerId / HardwareId / DeviceName）
与查询隔离键（`Device.OwnerId`，CONTEXT-MAP 不变量 2）**一概不动**；HardwareId
泛化理解为"主体的稳定标识"——桌面取 Windows MachineGuid，无头 hub 侧由配置给定
稳定串。绿场目标里的 `kind: machine | account | body` 列，等第二种主体真实入库
时再加。

### 2. Hub 是可复用的采集器宿主运行时；多实例星形，不嵌套

hub 的价值 = 本机信任域（loopback 不鉴权的合法性边界）+ 采集器托管 + 出网管线
（缓冲 / 退回重注入 / 快照压缩 / 声明上行，ADR-020/022/030）。它部署为 **N 个
对等实例**——桌面 Agent 是一个，server 旁的无头 hub 是另一个——各持自己的凭证
直连唯一的 ingest。拓扑是星形。

**否决嵌套**：hub → hub 链路跨机器即跨信任边界，必须鉴权，其协议与 hub → server
完全相同；"中心 hub"收到段之后能做的事（校验、upsert）都是 Analytics ingest 已
在做的事——两侧协议相同、无变换可做的层是空层。设备够不着服务端的未来场景由
**代理采集器**模式吸收（在可达机器的 hub 里装该设备的代理采集器），不改拓扑。

无头 hub 的形态：Generic Host 控制台，采集器为进程内 BackgroundService 直推
`ISegmentSink`，**无 loopback HTTP 面**（loopback 只为跨进程采集器存在）。复用
管线的前置机械工作：把可移植部分（UploadStream / JsonFileCache / TokenManager /
HeartbeatApiClient / DeclarationUplinkService / SegmentIngestService 的缓冲汇入
部分）从 net-windows 程序集抽到纯 net TFM 的 Hub.Core，`Heartbeat.Agent` 与无头
hub 共同引用。

### 3. Source 按观测者命名，不按产品

`system` / `browser` 本来就是观测者命名（browser 观测几百个产品）。VRChat 将来
可能有两个观测者：`vrchat.account`（无头 hub 轮询云 API：世界 → 实例）与
`vrchat.client`（PC 本机 OSC / 日志，目前只是假想）。source 是 ADR-030 声明的
主权单位——两个观测者的读数词汇、契约版本节奏、可靠性都不同，塞进一个 source
就得声明能力并集，契约演化被焊死。两轨对同一事实的独立证据**合法共存，摄入不
去重**；投影层合并是渲染决策，等两轨真实并存且造成阅读困扰时再做。

### 4. 设备归因是派生知识，不进事实层

"这次 VRChat 在 Quest 还是 PC"由时间线并置派生：PC 的 system 轨同时段有
VRChat.exe → PC；账号在线而 PC 轨没有 → 几乎必然 Quest。该推断归 Recap 叙事
（多设备分轨，ADR-023）；用户要钉死时经 Asking 确认为 Episode（ADR-031）。与
Anchor/Satellite 同哲学（ADR-029）：**能从时间线并置推出的语义，不写成存储字段**。

账号段的 source 不是 system，ADR-017 §4 统计边界自动挡双计——PC 上玩 VRChat 的
一小时，只有 system 的 VRChat.exe 段进时长，账号段是"在场证据"：进回放、进
Recap 投影（语义细节轨），不进报表求和。

### 5. 绿场靶子（记录方向，不排期）

从第一性重推的目标模型：Owner → Subject（machine | account | body）；事实三形状
——Segment（有身份的区间，快照生长）/ Event（无时长的时刻点，InputEvent 即其
第一个实例）/ Sample（无身份的数值序列：心率 gauge、步数 sum）；观测者声明从
读数深度表扩到事件种类 / 指标词表 / 轨互斥性。与现状 diff **全为加法**：Sample
一张新表、Subject 一个 kind 列、互斥性从硬编码 `source=='system'` 变声明字段。
逐项在对应需求真实到来时落地：Sample 等心率/步数真实造成困扰，kind 列等第二种
主体入库，互斥性声明等第二个互斥轨（如 vrchat.account）要进统计。

## Rejected alternatives

- **嵌套 hub**：中间层两侧协议相同、无变换可做，是仪式不是抽象；"中心 hub"命名
  还把 Analytics ingest 说成 Collection 拓扑的根，污染上下文边界。
- **Device "Quest 3" 直挂账号段**（本 ADR 讨论早期方案）：观测者证明不了硬件，
  制造假事实。
- **ObservedDevice 可空 + 段级手动设备标注**：动 `Device.OwnerId` 隔离承重梁；
  在采集器主权的事实层开手工涂改口，违反摄入"拒收不修复"纪律——用户确认的事实
  住知识层（Episode）。
- **Subject / CollectorHost 提前实体化**：单用户下 Subject ≡ Owner，为两个未落地
  数据源预建本体；CollectorHost 自认"不进活动语义"，那它就是 hub 配置，不进模型。
- **单一 `source = "vrchat"`**：迫使两个观测者共享一张深度表声明（能力并集），
  单边升级即撞车。

## Consequences

- ✅ 服务端**零改动**接入非驻留设备数据源——ADR-030 "服务端是语义无关层"这张
  支票的首次兑现。
- ✅ 统计边界（ADR-017 §4）、快照 upsert（ADR-018）、Recap 多设备轨（ADR-023）
  全部现成生效，无一需要修改。
- ✅ 绿场推导确认现有骨架即目标形状，后续演进全为加法，无拆迁。
- ⚠️ "设备"一词在 UI 继续使用；account 主体落地后，看板需从"设备即视图"演进为
  "我的一天"跨主体聚合 + 声明驱动的卡片可见性（另行设计，不在本 ADR）。
- ⚠️ Hub.Core 程序集拆分是无头 hub 的前置机械工作（搬移 + 理依赖，无设计决策）。
- ⚠️ 账号级采集器意味着第三方凭证（VRChat 账号）存放在无头 hub 配置中，凭证
  管理面扩大；单用户自部署下可接受，多用户化时必须重审。

## References

- [ADR-017](./017-activity-segment-pluggable-collectors.md) —— 采集器契约与统计边界 §4
- [ADR-018](./018-stable-segment-identity-snapshot-upload.md) —— 快照 upsert 天然支持轮询式采集
- [ADR-020](./020-system-collector-through-hub.md) / [ADR-022](./022-upload-stream-owns-reinjection.md) —— 被 Hub.Core 复用的出网管线
- [ADR-029](./029-observation-depth-matcher.md) —— Anchor/Satellite："并置可推的语义不进存储"
- [ADR-030](./030-collector-depth-declaration.md) —— source 为声明主权单位；服务端无关层
- [ADR-031](./031-hierarchical-strand-episode-teaching-loop.md) —— 设备归因钉死走 Episode
- `shared/CONTEXT.md` —— Device / Source 词条（随本 ADR 更新）
- `server/CONTEXT.md` —— Owner / Device 词条（随本 ADR 更新）
- `CONTEXT-MAP.md` —— Collection 上下文的 hub 拓扑描述（随本 ADR 更新）
