# ADR-041: 统一 Subject 与三类观测事实模型

## Status: Accepted

## Date: 2026-08-22

2026-09-09 修订：按 owner 决策删除 Fact 撤回。System、Browser、VRChat 正式采集器没有主动
产生撤回的业务路径；为假想删除保留状态、墓碑和防复活分支不符合最小事实模型。
Fact 始终是有 payload 的快照，保留身份、正常修订、乱序保护及同版本幂等/冲突规则。
此决定仅涉及采集事实，不改变其他领域的删除或 Package 版本撤回。

2026-09-09 再修订：按 owner 决策删除 Fact Schema 格式治理。Payload 保留可扩展 JSON；
不登记格式、不锁定格式版本或可变路径、不保存事实内容摘要。同版本一致性直接比较已保存内容。
此修订替代本 ADR 及 ADR-040/054 的 Schema 约束，不改变 Package 文件完整性检查。

## Context

现有主路径以 ActivitySegment 表示桌面活动，InputEvent 另走事件管道；VRChat 账号状态、心率和微信步数等未来来源既不一定由被观测设备本机采集，也不能都自然表达成活动区间。ADR-032 曾临时把 Device 放宽为“观测主体”，并把 Segment / Event / Sample 记作绿场方向，但这会让账号、身体和运行无头 Hub 的服务器继续借用机器语义，也没有给统一 Collector Protocol 一个稳定的输出契约。

OpenTelemetry、Prometheus/Grafana 与 ActivityWatch 的模型说明：区间、离散事件和数值观测可以共享少量信封字段，但它们的时间、身份、修订与聚合规则不能压成一个万能 JSON 事件。Heartbeat 因此统一“如何承载观测事实”，而不统一掉事实家族本身的语义。

## Decision

### 1. Subject 独立于采集宿主

Owner 拥有 Machine、Account、Person 等 Subject；每个 Collector Instance 观察一个 Subject，一个 Subject 可以由多个 Instance 观察。Hub Instance 是运维宿主，不参与事实归属，也不因为运行在某台服务器上就成为所托管事实的 Subject。

Device 回到 Machine Subject。VRChat 账号使用 Account Subject，心率等身体观测使用 Person Subject；不能为了复用 Device 字段制造未经观测的硬件归因。本节修订 ADR-032 将 Device 泛化为任意主体的临时方案。

### 2. 领域模型区分三类 Fact，协议按真实需求落地

- **Segment**：具有起止时间的区间事实；持续中的区间以同一 FactId、递增 Revision 的完整快照表达增长或纠正。
- **Event**：发生在一个时刻的离散事实；发生时间保持稳定，Payload 可用递增 Revision 修订；同一 Revision 的相同内容是幂等重放，不同内容是冲突。
- **Measurement**：数值状态或时间窗口内的数值总体；至少区分 Gauge、Sum 与 Histogram，并保留 unit、temporality、monotonic、reset 与 missing 等必要语义，不能退化为“时间戳 + 数字”。

ActivitySegment 是现有 Segment，InputEvent 是现有 Event。领域模型从一开始保留三类 Fact 的语义边界，不把零长度 Segment 当作所有事件的最终模型；但可执行 Collector Protocol v1 与 Manifest 当前只实现已有真实需求的 Segment 和 Event。Measurement 等第一个真实 Collector 出现时再进入 capability、wire model、Package 校验与投影，不为尚不存在的来源预建半套协议。`Sample` 更名为 `Measurement`，避免暗示它只能表示瞬时标量。

### 3. Fact Stream 承载稳定上下文，Fact 保持最小信封

Package 通过 Output Template 声明可产生的 FactKind、Source、SubjectKind 与低基数 identifying dimensions；Measurement Output 还必须声明完整的测量 descriptor。Hub 把模板绑定到具体 Subject 和 dimensions 后分配稳定 StreamId；Stream 跨 Activation 与兼容 Package 更新保持稳定，Subject、outputId、Measurement descriptor 或 identifying dimensions 改变时新建 Stream。

Stream 元数据持有 Subject、Collector Instance、Source、FactKind、Measurement descriptor（若适用）与 identifying dimensions。逐条 Fact 只携带 StreamId、Collector 生成的 UUIDv7 FactId、单调 Revision、该家族的事实时间、可选 ObservedAt 与JSON payload；Hub 把 ReceivedAt 与 ActivationId 记录在独立 ingest metadata 中，不改写 wire Fact。ActivationId 只作为本次 writer 的 provenance，不进入 Stream 身份。

协议按 `StreamId + FactId + Revision` 幂等收敛。Segment 起点、Event 发生时间不变；Collection/Runtime
负责 final Segment 不能重开，Analytics 按 ADR-055 只校验其持久保管的时间与 Payload，不存终态；
Payload 的新增字段或修订不需要登记、版本基线或通用演进规则。消费者按业务字段判断能否投影，
不适用的事实仍完整保管。Fact 不携带 `recordState`；旧撤回输入仍明确拒绝。

## Consequences

- ✅ 桌面活动、账号在线和输入事件进入同一 Collector Protocol；未来心率和步数仍有明确的 Measurement 扩展位置，不会被强塞成 Segment 或 Event。
- ✅ Hub 的运行位置与事实主体解耦；一个常驻 Hub 可以如实托管多个账号、机器或人的 Collector Instance。
- ✅ 公共信封保持小而稳定，Collector 的 Payload 可以按实际需要扩展。
- ✅ 明确的 FactId / Revision 规则为重试、乱序和修正提供统一幂等基础。
- ⚠️ Device 不再能承担所有查询隔离与分组语义；引入第二种 Subject 时需要实际 schema 迁移。
- ⚠️ Analytics 最终需要分别持久化或投影三类 Fact，不能继续假设所有输入都是 ActivitySegment。
- ⚠️ Measurement 目前只有领域语义承诺，没有可执行协议兼容承诺；引入首个 Measurement Collector 时必须完整设计 capability、wire、存储与查询语义。

## References

- [ADR-017](./017-activity-segment-pluggable-collectors.md) — ActivitySegment 与 Source/Subject 区分的起点
- [ADR-018](./018-stable-segment-identity-snapshot-upload.md) — Segment 稳定身份与快照增长
- [ADR-032](./032-device-as-observed-subject.md) — Subject 与三类事实的绿场推导，本 ADR 固化并修订 Device 语义
- [Collector Fact Contracts](../../collection/contracts/README.md) — 当前 Fact 与 Package 边界
- [系统架构与协议](../architecture/system-overview.md) — 当前协议、Package 与 Fact 契约的权威关系
