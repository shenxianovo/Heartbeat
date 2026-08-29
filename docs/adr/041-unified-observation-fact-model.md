# ADR-041: 统一 Subject 与三类观测事实模型

## Status: Accepted

## Date: 2026-08-22

## Context

现有主路径以 ActivitySegment 表示桌面活动，InputEvent 另走事件管道；VRChat 账号状态、心率和微信步数等未来来源既不一定由被观测设备本机采集，也不能都自然表达成活动区间。ADR-032 曾临时把 Device 放宽为“观测主体”，并把 Segment / Event / Sample 记作绿场方向，但这会让账号、身体和运行无头 Hub 的服务器继续借用机器语义，也没有给统一 Collector Protocol 一个稳定的输出契约。

OpenTelemetry、Prometheus/Grafana 与 ActivityWatch 的模型说明：区间、离散事件和数值观测可以共享少量信封字段，但它们的时间、身份、修订与聚合规则不能压成一个万能 JSON 事件。Heartbeat 因此统一“如何承载观测事实”，而不统一掉事实家族本身的语义。

## Decision

### 1. Subject 独立于采集宿主

Owner 拥有 Machine、Account、Person 等 Subject；每个 Collector Instance 观察一个 Subject，一个 Subject 可以由多个 Instance 观察。Hub Instance 是运维宿主，不参与事实归属，也不因为运行在某台服务器上就成为所托管事实的 Subject。

Device 回到 Machine Subject。VRChat 账号使用 Account Subject，心率等身体观测使用 Person Subject；不能为了复用 Device 字段制造未经观测的硬件归因。本节修订 ADR-032 将 Device 泛化为任意主体的临时方案。

### 2. 领域模型区分三类 Fact，协议按真实需求落地

- **Segment**：具有起止时间的区间事实；持续中的区间以同一 FactId、递增 Revision 的完整快照表达增长、纠正或撤回。
- **Event**：发生在一个时刻的离散事实；默认不可变，同一 FactId 的相同内容是幂等重放，不同内容是冲突。
- **Measurement**：数值状态或时间窗口内的数值总体；至少区分 Gauge、Sum 与 Histogram，并保留 unit、temporality、monotonic、reset 与 missing 等必要语义，不能退化为“时间戳 + 数字”。

ActivitySegment 是现有 Segment，InputEvent 是现有 Event。领域模型从一开始保留三类 Fact 的语义边界，不把零长度 Segment 当作所有事件的最终模型；但可执行 Collector Protocol v1 与 Manifest 当前只实现已有真实需求的 Segment 和 Event。Measurement 等第一个真实 Collector 出现时再进入 capability、wire model、Package 校验与投影，不为尚不存在的来源预建半套协议。`Sample` 更名为 `Measurement`，避免暗示它只能表示瞬时标量。

### 3. Fact Stream 承载稳定上下文，Fact 保持最小信封

Package 通过 Output Template 声明可产生的 FactKind、Source、schema、SubjectKind 与低基数 identifying dimensions；Measurement Output 还必须声明完整的测量 descriptor。Hub 把模板绑定到具体 Subject 和 dimensions 后分配稳定 StreamId；Stream 跨 Activation 与兼容 Package 更新保持稳定，Subject、outputId、Schema Major、Measurement descriptor 或 identifying dimensions 改变时新建 Stream。

Stream 元数据持有 Subject、Collector Instance、Source、FactKind、schema、Measurement descriptor（若适用）与 identifying dimensions。逐条 Fact 只携带 StreamId、实际 SchemaRevision、Collector 生成的 UUIDv7 FactId、单调 Revision、该家族的事实时间、可选 ObservedAt 与类型化 payload；Hub 把 ReceivedAt 与 ActivationId 记录在独立 ingest metadata 中，不改写 wire Fact。ActivationId 只作为本次 writer 的 provenance，不进入 Stream 身份。

Fact Schema 使用 SchemaId + SchemaMajor 表示语义兼容线，以 SchemaRevision + SchemaHash 锁定同一 Major 内的兼容扩展。破坏性变化必须提高 Major 并创建新 Stream。协议按 `StreamId + FactId + Revision` 幂等收敛，但 Segment、Event 与 Measurement 各自决定高 Revision 是否合法以及如何解释。

## Consequences

- ✅ 桌面活动、账号在线和输入事件进入同一 Collector Protocol；未来心率和步数仍有明确的 Measurement 扩展位置，不会被强塞成 Segment 或 Event。
- ✅ Hub 的运行位置与事实主体解耦；一个常驻 Hub 可以如实托管多个账号、机器或人的 Collector Instance。
- ✅ 公共信封保持小而稳定，类型语义和 schema 可以独立演进。
- ✅ 明确的 FactId / Revision 规则为重试、乱序和修正提供统一幂等基础。
- ⚠️ Device 不再能承担所有查询隔离与分组语义；引入第二种 Subject 时需要实际 schema 迁移。
- ⚠️ Analytics 最终需要分别持久化或投影三类 Fact，不能继续假设所有输入都是 ActivitySegment。
- ⚠️ Measurement 目前只有领域语义承诺，没有可执行协议兼容承诺；引入首个 Measurement Collector 时必须完整设计 capability、wire、schema、存储与查询语义。

## References

- [ADR-017](./017-activity-segment-pluggable-collectors.md) — ActivitySegment 与 Source/Subject 区分的起点
- [ADR-018](./018-stable-segment-identity-snapshot-upload.md) — Segment 稳定身份与快照增长
- [ADR-032](./032-device-as-observed-subject.md) — Subject 与三类事实的绿场推导，本 ADR 固化并修订 Device 语义
- [Collector Fact Contracts](../../collection/contracts/README.md) — 当前可执行 Fact payload 与演进规则
- [系统架构与协议](../architecture/system-overview.md) — 当前协议、Package 与 Fact 契约的权威关系
