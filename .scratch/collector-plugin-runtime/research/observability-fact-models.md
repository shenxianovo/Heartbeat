# Observability fact models：Segment / Event / Sample 的一手资料调查

调查日期：2026-08-17  
范围：Prometheus、OpenTelemetry、Grafana、Loki、Tempo、Mimir 的官方规范、官方文档与一手源码。本文只提炼可供 Heartbeat 设计参考的事实，不把这些系统的实现约束直接当作 Heartbeat 需求。

## 结论

`Segment / Event / Sample` 是合理的 Heartbeat 顶层时间事实分类，但不宜表述成业界已经标准化的“三种核心事实形状”。可观测性领域更常说 traces / logs / metrics 三种信号；它们的数据形状大致映射为：

| Heartbeat 形状 | 最接近的一手模型 | 关键修正 |
| --- | --- | --- |
| `Segment` | OpenTelemetry Span | 有 start/end 的区间事实；但 Heartbeat 的活动段不是分布式调用 span，不应被迫拥有 trace tree、parent 或 SpanKind。 |
| `Event` | OpenTelemetry LogRecord、Span Event；Loki log entry | 离散时刻事实；发生时间与采集系统观察时间应分开。事件类型不等于事件 occurrence identity。 |
| `Sample` | Prometheus sample、OpenTelemetry Metric Point | 不能只定义成“时间戳 + 数字”。Gauge 接近时点读数；delta/cumulative Sum 和 Histogram 可能描述 `(start, end]` 上的聚合事实。 |

因此建议保留三类公共能力，但把 `Sample` 的正式协议含义写成 **Metric Point 家族**。三分法覆盖“区间、离散发生、数值测量/聚合”这一有用的产品语言；修订、撤销、迟到、缺失和 provenance 则是三者共享的摄入语义，不应伪装成第四种业务事实。

## Prometheus：所有东西最终都是 time series sample

Prometheus 的持久模型不是 Event/Span/Sample 联合模型。它把所有数据表示成 time series：序列身份由 metric name 和完整 label set 决定；每个 sample 是 `float64` 或 native histogram value，加一个毫秒时间戳。[Prometheus data model](https://prometheus.io/docs/concepts/data_model/)

这证明数值采样是一等形状，但也暴露了不可照搬之处：

- Counter、Gauge、Histogram、Summary 是采集端暴露的不同 metric type；Prometheus 服务端的时间序列模型不会替业务层保存一个通用“事实对象”。Counter 只能增长但允许 reset，Gauge 可升降，Histogram 表示一组观测的分布。[Metric types](https://prometheus.io/docs/concepts/metric_types/)
- Counter 的下降会被 `rate()` / `resets()` 解释成 reset；正确求 rate 依赖“这是 counter”的语义和连续点，而不是单个 `(time, value)`。[`rate()` 与 `resets()`](https://prometheus.io/docs/prometheus/latest/querying/functions/#rate)
- 序列不再出现时不是数值 0。Prometheus 使用 lookback 和 stale marker 让旧序列从查询结果消失；默认 lookback 为 5 分钟。[Staleness](https://prometheus.io/docs/prometheus/latest/querying/basics/#staleness)
- 每个 label 组合都是额外序列。官方明确警告不要用 user ID、email 等无界值作 label，并建议大多数 metric 保持极低基数。[Instrumentation / label cardinality](https://prometheus.io/docs/practices/instrumentation/#do-not-overuse-labels)、[naming guidance](https://prometheus.io/docs/practices/naming/#labels)
- Prometheus 可以配置 `out_of_order_time_window` 接收窗口内乱序点；这是一项存储策略，不是时间事实的固有性质。官方的 OTel 接入指南也说明 batching 和多 Collector replica 会造成乱序。[Prometheus OTel guide](https://prometheus.io/docs/guides/opentelemetry/#enable-out-of-order-ingestion)
- 当前 TSDB 实现允许同一序列、同一时间戳、同一值的精确重放；同一时间戳不同值是冲突。这个规则可从官方源码的 [`ErrDuplicateSampleForTimestamp`](https://github.com/prometheus/prometheus/blob/main/storage/interface.go#L30-L40) 及 [Head append 判定](https://github.com/prometheus/prometheus/blob/main/tsdb/head_append.go#L650-L692) 验证。

对 Heartbeat 的借鉴：

1. `Sample` 必须声明 measurement kind。心率通常是 Gauge；“当天微信步数”更像会在日界线或数据源重置时 reset 的 cumulative monotonic Sum；一段时间内的心率分布可能是 Histogram。三者不能只靠 `value` 猜测。
2. Sample stream 的语义身份应稳定，例如 `Subject + CollectorInstance + metric name + bounded dimensions`。`ActivationId` 应作为 provenance，而不应进入 stream identity；否则每次重启都会制造新序列并掩盖 writer overlap。
3. 缺样、数据源 stale、Collector unavailable 与测量值 0 必须分开。
4. 不要把 URL、窗口标题、世界名、事件 ID 或任意 attributes 全部提升为可索引 dimensions。Heartbeat 可以保存高基数 payload，但索引维度需要单独、受控的 vocabulary。

## OpenTelemetry：三种信号接近三种形状，但 Metric Point 也有区间

### Span

OpenTelemetry Span 有不可变的 `TraceId + SpanId` 上下文身份、start/end、attributes、parent、links 和 timestamped events。结束后不得再修改；start/end 表示操作的 elapsed real time。[Tracing API — Span](https://opentelemetry.io/docs/specs/otel/trace/api/#span)

这支持 `Segment` 作为区间事实，但不能原样照搬：Heartbeat ActivitySegment 不是调用链节点，而且当前 [ADR-018](../../../docs/adr/018-stable-segment-identity-snapshot-upload.md) 允许正在进行的同一 Segment 以相同 Id 上传持续增长的 snapshot。OTel 的“End 后不可变”适合完成的 telemetry span，不适合直接替换 Heartbeat 的 open-activity snapshot/upsert 契约。

Span Event 由 name、timestamp、attributes 组成。自定义 timestamp 可以导致事件按记录顺序乱序，甚至落在 span start/end 之外；规范不要求归一化。[Tracing API — Add Events](https://opentelemetry.io/docs/specs/otel/trace/api/#add-events) 这说明 Event 不应被强制嵌套进 Segment；确有归属或因果关系时再用显式 correlation/link。

### Log / Event Record

OpenTelemetry LogRecord 明确区分：

- `Timestamp`：事件在源头发生的时间；
- `ObservedTimestamp`：采集系统观察到它的时间；
- `EventName`：事件类别/结构的名称；
- 可选的 TraceId / SpanId、Resource、InstrumentationScope、Attributes 和 Body。

来源：[OpenTelemetry Logs Data Model](https://opentelemetry.io/docs/specs/otel/logs/data-model/)。字段表没有通用 occurrence ID；`EventName` 是类别，不是某一次发生的唯一 ID。OTLP 又明确允许重试造成重复数据，并不提供端到端 exactly-once。[OTLP duplicate data](https://opentelemetry.io/docs/specs/otlp/#duplicate-data)

对 Heartbeat 的直接含义是：`EventName` 不能代替 `FactId`；事件必须拥有 Collector 生成且跨 retry/rebatch/Hub restart 稳定的 occurrence identity。`OccurredAt` 与 `ObservedAt` 应分别保留，才能表达 API 延迟、设备离线补传和源时钟偏差。

### Metric Point

OpenTelemetry Metrics 把 API recording event、传输中的 Metric Stream、backend timeseries 分开。每条 stream 具有 Resource、Instrumentation Scope、name、point kind、unit，以及适用时的 temporality 和 monotonic 等身份；点由 attributes 进一步区分。[Metrics Data Model](https://opentelemetry.io/docs/specs/otel/metrics/data-model/)

Metric Point 并非统一的瞬时标量：

- Gauge 是某收集区间里最后观测的值；
- Sum 有 `Delta | Cumulative` temporality 以及 monotonic 标志；
- Histogram / ExponentialHistogram 是某个时间区间内 population 的压缩，至少携带 count、sum 和 buckets；
- point 有 `TimeUnixNano`；Sum/Histogram 强烈依赖 `StartTimeUnixNano` 来解释连续序列、reset 与 gap；cumulative 覆盖 `(T0, Tn]`，delta 覆盖连续的 `(Tn-1, Tn]`；
- `No recorded value` 可表达序列不存在/停止报告，而不是 0；
- 一个 stream 应只有一个 logical writer；多 writer overlap 会让 cumulative sum 呈现为连续 reset。

相关章节：[Metric points](https://opentelemetry.io/docs/specs/otel/metrics/data-model/#metric-points)、[Temporality](https://opentelemetry.io/docs/specs/otel/metrics/data-model/#temporality)、[Single-writer](https://opentelemetry.io/docs/specs/otel/metrics/data-model/#single-writer)、[Resets and gaps](https://opentelemetry.io/docs/specs/otel/metrics/data-model/#resets-and-gaps)。其中基础 Metric Stream/Point 语义稳定，但 resets/gaps/overlap 的部分章节仍标为 Development；可借鉴问题分解，不应逐字宣称为 Heartbeat 已稳定协议。

因此 `samples.v1` 若只有 `{ timestamp, value }` 会立即丢失步数 reset、delta 区间、Histogram 和 missing 等关键语义。至少应为未来保留 `Gauge | Sum | Histogram`、`StartTime`、`Time`、`Delta | Cumulative`、`Monotonic`、`Unit`、dimensions、stale/missing 和 provenance。

## Grafana 生态没有一套跨产品统一的持久事实模型

### Grafana

Grafana 支持许多拥有不同模型的数据源，并把查询结果统一转换成 **Data Frame**：等长、强类型 fields 组成的列式容器，可承载 time series、numeric、heatmap、logs 等结果。[Grafana Data Frames](https://grafana.com/developers/plugin-tools/key-concepts/data-frames)

Data Frame 是查询结果/可视化交换结构，不是业务事实语义或统一写入存储模型。Heartbeat 可以借鉴“公共 envelope + 类型化结果投影”，但不应把通用表格容器当 Collector ingest contract。

### Loki

Loki 的核心是 log stream：相同 label set 的 log entries 组成一条 stream；每个 entry 经 push API 携带纳秒 timestamp、log line 和可选 structured metadata。[Loki HTTP API](https://grafana.com/docs/loki/latest/reference/loki-http-api/#post-lokiapiv1push) Loki 只索引 stream labels，不索引 log line；官方要求 labels 表达低基数来源，把高基数字段放 structured metadata。[Loki labels](https://grafana.com/docs/loki/latest/get-started/labels/)

Loki 默认允许有限窗口内的 out-of-order entries；同 stream 过旧的 entry 会被拒绝。它还有可选 `increment_duplicate_timestamp`，只为同一 push 中同 timestamp、不同 log line 做纳秒偏移，而且官方明确说该机制在跨 push 和乱序场景并不完整。[Loki configuration](https://grafana.com/docs/loki/latest/configure/#limits_config) 删除则是按 stream selector、时间窗和可选 line filter 发起的异步删除，不是按稳定 EventId 的通用 revision/upsert。[Log entry deletion](https://grafana.com/docs/loki/latest/operations/storage/logs-deletion/)

结论：Loki 适合借鉴“低基数索引 + 高基数 metadata”和迟到窗口，不适合拿来定义 Heartbeat 的事件身份或修订语义。

### Tempo

Tempo 使用 trace/span 模型。Trace 是 span tree；span 有 name、duration、status、kind 与 attributes，duration 是 end-start，核心字段来自 OpenTelemetry。[Tempo trace structure](https://grafana.com/docs/tempo/latest/introduction/trace-structure/)

Tempo 不是为所有事实建立一个新统一模型，而是保存并查询 tracing 信号。Heartbeat Segment 可借鉴 stable ID、区间和可选 links；不应引入无业务价值的 trace root/parent tree。

### Mimir

Mimir 是 Prometheus 与 OpenTelemetry metrics 的长期存储，兼容 Prometheus remote write、PromQL 和 alerting。[Mimir introduction](https://grafana.com/docs/mimir/latest/introduction/) 它允许按配置窗口接收 out-of-order samples，并明确指出迟到写入会改变已经缓存的查询结果和 recording rule 结果。[Mimir out-of-order ingestion](https://grafana.com/docs/mimir/latest/configure/configure-out-of-order-samples-ingestion/)

Mimir 同样不是跨 logs/traces/metrics 的统一事实模型；它延续 time-series 语义。同序列同 timestamp 不同 value 会作为 duplicate timestamp error 拒绝。[Mimir runbook](https://grafana.com/docs/mimir/latest/manage/mimir-runbooks/#err-mimir-sample-duplicate-timestamp)

Grafana + Loki + Tempo + Mimir 的实际组合反而证明：可以共享可视化容器、Resource/labels/attributes 与 cross-signal correlation，但 logs、traces、metrics 仍保留各自的身份、时间和修订规则。

## Heartbeat 应借鉴的契约

### 1. 所有事实共享 envelope，不共享一个模糊 payload

建议三类事实共同拥有：

- 稳定 `FactId`：同一次 occurrence 跨重试不变；
- `SubjectId`：事实归属，不由 Hub Instance 代替；
- `CollectorInstanceId`：稳定生产者身份；
- `ActivationId`：只作运行 provenance 和 overlap 诊断，不改变业务事实/stream identity；
- fact kind + schema/version discriminator；
- source occurrence time 与 Hub observed/received time；
- 明确 revision/conflict/tombstone 字段或等价语义。

类型化 payload 再分别表达 Segment start/end、Event timestamp/body、Sample metric kind/temporality/value。这样既统一摄入纪律，又不把 Histogram 压成 number、把 Segment 压成两个互不相关事件。

### 2. 时间戳必须回答“谁的钟、什么时候的事”

- Event 至少分 `OccurredAt` 与 `ObservedAt`；Segment 至少有 source `StartTime/EndTime` 和 observed/ingested provenance。
- Sample 的 `Time` 是 measurement 生效/区间结束时刻；累加与分布点还需要 `StartTime`。
- Wire 统一 UTC instant 与精度/截断规则；时区偏移如有产品价值应单独保存，不能混进 instant。
- 区间边界规则必须明确。OTel metric interval 用 `(start, end]`，PromQL range selector也是左开右闭；Heartbeat 当前 ActivitySegment 查询采用 overlap，并不要求复制该闭开规则。最重要的是每种 payload 明确且测试边界，不能默认它们相同。

### 3. 幂等、冲突和修订不能依赖底层可观测性后端猜测

建议默认规则：

- Event：同 `FactId`、同内容为 idempotent replay；同 `FactId`、不同内容为 conflict，除非协议显式携带更高 revision。
- Sample：精确重复可以幂等；同一 stream/time 的不同 value 不应静默 last-write-wins。若源系统会修正历史读数，必须携带稳定 FactId/revision 或显式 correction。
- Segment：保留 ADR-018 的稳定 activity identity 与增长 snapshot 优点，但“attributes last-write-wins”在乱序重试下并不天然交换律。下一版需在以下两者中明确选择：仅允许可证明单调合并的字段；或为 snapshot 增加 revision/observed sequence，使旧 snapshot 不能覆盖新 attributes。关闭、改边界、合并、撤销也需要显式语义。
- Tombstone/retraction 是摄入协议语义；Loki 的按查询删除和 Prometheus 的 stale 都不能替代它。

### 4. 迟到窗口是产品策略，不能照搬 Prometheus 的分钟级默认

Heartbeat 的离线设备、第三方 API 回补和全天候 server Collector 可能产生小时或天级迟到。应按 fact type/source 定义：最大可接受迟到、超窗后 reject 还是进入 reconciliation、何时令日报/Recap 缓存失效、修订窗口何时关闭。Mimir 的经验尤其重要：接受乱序意味着已缓存查询和派生结果也可能过时。

### 5. Sample 的最小语义问题必须在公共协议冻结前回答

下一轮设计至少需要裁决：

1. `samples.v1` 是否首版就正式支持/保留 `Gauge | Sum | Histogram`、temporality、start time、monotonic、unit 和 missing；还是首版明确只实现 Gauge，但保证未来能非破坏扩展？
2. Segment/Event/Sample 是否统一要求稳定 `FactId`？同 ID 不同内容是 reject conflict，还是允许严格单调 revision？
3. Sample stream 的 identifying dimensions 与普通 attributes 如何分开，并由谁限制基数？
4. 当前 ADR-018 的 growing snapshot 如何阻止迟到旧 snapshot 回退 attributes，并怎样表达 close/correct/retract？

## 最终判断

可以确认 Q16 的方向：Collector Protocol 与 manifest 应从一开始承认 `segments.v1`、`events.v1`、`samples.v1` 等不同输出能力，不能固化成 `/v1/segments` 的别名。但需要把措辞从“可观测性的三种标准核心事实形状”收紧为：**Heartbeat 选择的三种核心时间事实家族，与 tracing/logging/metrics 的主流形状高度对应。**

最关键的防错点是：`Sample` 不只是 instant scalar；`EventName` 不等于 EventId；absence 不等于 zero；标签不是任意 metadata；out-of-order 接收会使派生结果需要重算；revision/upsert 是 Heartbeat 自己必须定义的领域协议，Prometheus、Loki、Tempo 或 OTLP 都不会替它提供 exactly-once 与通用事实修订。
