# Fact Model v1

Status: Draft 0.2

本规范定义 Collector Protocol v1 中的 Fact Stream、公共 Fact 信封，以及 Segment、Event、Measurement 三种事实家族。它是 [ADR-041](../../../docs/adr/041-unified-observation-fact-model.md) 的可实现草案，不规定 Analytics 的物理存储表。承载这些 Fact 的消息、握手与 ACK 语义见 [Collector Protocol v1](./collector-protocol-v1.md)；产品边界见 [PRD](../PRD.md)。

本文使用“必须 / 不得 / 应 / 可以”表达规范强度。仍待裁决、会改动本规范的问题集中在 [§12 未决问题](#12-未决问题)。

## 1. 核心不变量

1. Fact 描述 Collector 对 Subject 作出的时间观测；Hub、Package、配置、命令和派生知识不是 Fact。
2. Subject 与 Collector 运行位置独立。运行无头 Hub 的服务器不自动成为它所托管事实的 Subject。
3. Stream 保存稳定上下文；逐 Fact 不重复携带 Subject、Instance、Source 和 schema。
4. `StreamId + FactId` 标识一个逻辑事实，`Revision` 标识其内容版本。
5. 重试、重新分批、Hub 重启或 Activation 变化不得改变 FactId。
6. 旧 Revision 不得覆盖新 Revision；同 Revision 的不同 canonical 内容是冲突。
7. Segment、Event、Measurement 共享信封，但各自定义时间与合法演进，不能互相冒充。
8. `ObservedAt` 是 Collector 获知事实的时间；`ReceivedAt` 由 Hub 记录；二者都不替代事实发生时间。

## 2. Subject

协议中的 Subject 引用：

```json
{
  "subjectId": "0198d5df-5df3-70a1-937d-68a7d64623e2",
  "kind": "machine"
}
```

v1 固定 `kind`：

- `machine`：计算设备；现有 Device 属于此类。
- `account`：VRChat、微信等外部账号。
- `person`：心率、身体状态等直接以人为主体的观测。

SubjectId 由 Hub 的本地 Desired State 引用或绑定，不由 Collector 在 Fact 中临时声明。

## 3. Fact Stream

Fact Stream 是 Output Template 对具体 Instance、Subject 和 identifying dimensions 的稳定绑定。

```json
{
  "streamId": "0198d5e2-e0d4-7b30-9da7-342ee261bf62",
  "collectorInstanceId": "0198d5e0-5d15-73d8-a6d8-84a50ddf855f",
  "subject": {
    "subjectId": "0198d5df-5df3-70a1-937d-68a7d64623e2",
    "kind": "machine"
  },
  "outputId": "tabs",
  "source": "browser",
  "factKind": "segment",
  "schema": {
    "id": "heartbeat.browser.tab",
    "major": 1,
    "revision": 1,
    "hash": "sha256:..."
  },
  "dimensions": {}
}
```

### 3.1 Stream identity

以下字段共同决定是否仍是同一 Stream：

- CollectorInstanceId
- SubjectId
- OutputId
- Source
- FactKind
- SchemaId + SchemaMajor
- Measurement descriptor（仅 Measurement）
- canonical identifying dimensions

ActivationId、PackageVersion、SchemaRevision、显示名称和高基数 payload 不进入 Stream identity。

兼容 Package 更新和 Activation 重建必须复用 StreamId。任一 identity 字段变化必须创建新 Stream，不得把旧 Stream 的语义原地改写。

### 3.2 Identifying dimensions

Dimensions 的 key 必须由 Output Template 预先声明，值在打开 Stream 时绑定。v1 的值只允许非 null JSON string：Hub 先做 Unicode NFC 规范化，再按 key ordinal 排序并以规范化结果计算 Stream identity。它们必须是稳定、低基数、确实影响序列身份的属性；number、boolean、array 与 object 一律拒绝。Dimension key 必须匹配 `^[a-z][a-zA-Z0-9]*$`，由 Output Template 固定，不做 NFC 规范化。

> **未决**：“低基数”目前只是道德约束。v1 没有给出 value 长度上限、单 Stream dimension 数量上限、单 Output Template 允许的活跃 Stream 数上限，也没有对应的拒绝码，Hub 无从拦截基数爆炸——而这恰好是 research 里点名的经典失败模式。见 [PRD 草案暴露的矛盾第 8 条](../PRD.md#草案暴露的矛盾待裁决)。

不得把 URL、窗口标题、世界实例 ID、文件路径等高基数事实内容放进 dimensions；这些内容属于 typed payload。一个动态值不应仅因“希望查询”就升级为 Stream identity。

### 3.3 Measurement descriptor

Measurement Stream 额外固定测量语义：

```json
{
  "measurement": {
    "type": "sum",
    "unit": "1",
    "temporality": "cumulative",
    "monotonic": true
  }
}
```

| 字段 | Gauge | Sum | Histogram |
| --- | --- | --- | --- |
| `type` | `gauge` | `sum` | `histogram` |
| `unit` | 必须 | 必须 | 必须 |
| `temporality` | 固定 `instant` | `delta` 或 `cumulative` | `delta` 或 `cumulative` |
| `monotonic` | 不允许 | 必须 | 不允许 |
| `bounds` | 不允许 | 不允许 | 必须，严格递增 |

`unit` 在一个 Stream 内不可改变，应使用 UCUM 兼容写法；无量纲使用 `1`。整个 Measurement descriptor 属于 Stream identity；改变 type、unit、temporality、monotonic 或 Histogram bounds 必须创建新 Stream。

## 4. 公共 Fact 信封

```json
{
  "streamId": "0198d5e2-e0d4-7b30-9da7-342ee261bf62",
  "schemaRevision": 1,
  "factId": "0198d5eb-fc31-7d7b-8bf0-c2d009ec8999",
  "revision": 3,
  "observedAt": "2026-08-22T12:01:00Z",
  "recordState": "present",
  "time": {},
  "payload": {}
}
```

| 字段 | 必须性 | 语义 |
| --- | --- | --- |
| `streamId` | 必须 | 指向已打开的 Fact Stream |
| `schemaRevision` | 必须 | 整个 FactSubmission 实际遵循的同 Major Fact Schema revision；Hash 由 Stream schema catalog 解析 |
| `factId` | 必须 | Collector 首次产生事实时生成的 UUIDv7 |
| `revision` | 必须 | Collector 从 `1` 开始生成、之后严格递增的正整数；传输可以跳过中间 Revision |
| `observedAt` | 可选 | Collector 获知此 Revision 的时间 |
| `recordState` | 必须 | `present` 或 `retracted` |
| `time` | 必须 | 由 FactKind 决定的事实时间对象 |
| `payload` | 条件必须 | `present` 时必须；`retracted` 时省略 |

Collector wire shape 称为 `FactSubmission`。Hub 持久接收后形成内部 `IngestedFact`，服务器字段进入独立 metadata，不修改原 FactSubmission 的顶层 shape：

```json
{
  "fact": {
    "streamId": "0198d5e2-e0d4-7b30-9da7-342ee261bf62",
    "schemaRevision": 1,
    "factId": "0198d5eb-fc31-7d7b-8bf0-c2d009ec8999",
    "revision": 3,
    "recordState": "present",
    "time": {},
    "payload": {}
  },
  "ingestMetadata": {
    "receivedAt": "2026-08-22T12:01:00.123Z",
    "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c"
  }
}
```

`ingestMetadata.activationId` 是 provenance；同一 Fact 的更高 Revision 可以来自新的 Activation。ReceivedAt 只描述进入当前 Hub 的时间，不参与事实排序或去重。

### 4.1 Retraction

`recordState: retracted` 表示 Collector 撤回整个 FactId，而不是把 payload 更新为空。它必须使用更高 Revision，保留原 FactKind 要求的 `time`，并把 `schemaRevision` 指向明确允许该 retraction 的精确 Fact Schema revision，便于验证、审计与投影失效。

v1 的 Segment Fact Schema 必须声明 `evolution.mode: segmentSnapshot` 与 `evolution.allowRetraction: true`；Event 与 Measurement 只有其 Fact Schema 的 `evolution.allowRetraction` 为 `true` 时才允许。重用已撤回的 FactId 恢复为 `present` 不允许；需要恢复时生成新 FactId。

## 5. Segment

Segment 表示具有持续时间的区间事实。

### 5.1 时间对象

```json
{
  "time": {
    "start": "2026-08-22T11:59:30Z",
    "end": "2026-08-22T12:01:00Z",
    "isFinal": false
  }
}
```

- `start` 在一个 FactId 的全部 Revision 中必须保持不变。
- `end >= start`。
- `isFinal: false` 表示当前完整快照的观测终点，未来可能继续增长。
- `isFinal: true` 表示 Collector 已确认该 Segment 结束；之后仍可以用更高 Revision 纠错或撤回，但不得再次改回 `isFinal: false`。名称特意避开 `closed`，防止与数学上的区间开闭混淆。

### 5.2 Revision 规则

Segment 的每个 Revision 都是完整快照，不是 patch：

- 正常生长：`start` 不变，`end` 单调增加；
- 封口：最后一个开放快照之后提交 `isFinal: true`；
- 纠错：允许更高 Revision 修改 `end` 或 payload，但必须保留 FactId 与 start；
- 撤回：更高 Revision 使用 `recordState: retracted`。

Hub 不得根据 payload 相等、时间相邻或 Source 猜测两个 FactId 应被合并。Collector SDK 可以提供 pulse/extend API，但 wire 上发送的仍是完整 Segment snapshot。

### 5.3 Browser Segment 示例

Stream：`source=browser`，`schema=heartbeat.browser.tab/1`，Subject 为 Machine。

```json
{
  "streamId": "0198d5e2-e0d4-7b30-9da7-342ee261bf62",
  "schemaRevision": 1,
  "factId": "0198d5eb-fc31-7d7b-8bf0-c2d009ec8999",
  "revision": 3,
  "observedAt": "2026-08-22T12:01:00Z",
  "recordState": "present",
  "time": {
    "start": "2026-08-22T11:59:30Z",
    "end": "2026-08-22T12:01:00Z",
    "isFinal": false
  },
  "payload": {
    "url": "https://example.com/docs",
    "site": "example.com",
    "title": "Example Docs",
    "windowId": 42,
    "appHint": "edge"
  }
}
```

当前 `IdentityKey` 不进入公共信封。browser schema 可以从规范化 URL 定义自己的 activity key，也可以在 payload 中显式保留 `activityKey`；它是该 schema 的语义，不是所有 Fact 的共同身份。

### 5.4 VRChat Segment 示例

Stream：`source=vrchat.account`，`schema=heartbeat.vrchat.presence/1`，Subject 为 Account。

```json
{
  "streamId": "0198d5f2-1a11-740d-a2b7-bb3c29176a91",
  "schemaRevision": 1,
  "factId": "0198d5f2-1c67-73c8-a07e-70b226251280",
  "revision": 5,
  "observedAt": "2026-08-22T12:05:00Z",
  "recordState": "present",
  "time": {
    "start": "2026-08-22T11:35:00Z",
    "end": "2026-08-22T12:05:00Z",
    "isFinal": false
  },
  "payload": {
    "worldId": "wrld_...",
    "worldName": "Example World",
    "instanceId": "12345~region(us)"
  }
}
```

运行 VRChat Collector 的服务器不是 Subject，Quest/PC 归因也不写入 payload，除非 Collector 真的观测到了设备证据。

## 6. Event

Event 表示发生在一个时刻、没有持续时间的离散事实。

### 6.1 时间对象

```json
{
  "time": {
    "occurredAt": "2026-08-22T12:03:14.225Z"
  }
}
```

Event 默认不可变：

- 首次提交必须是 Revision `1`；
- 相同 canonical 内容的 Revision `1` 重放是 duplicate；
- 同一 FactId 的不同内容是 conflict；
- 只有 Fact Schema 声明 `evolution.mode: mutableEvent` 时才允许更高的 `recordState: present` Revision，并必须用 `mutablePayloadPaths` 定义允许修改的字段；`evolution.allowRetraction: true` 可以独立允许一次终结性的 `recordState: retracted` Revision，不要求 mutableEvent。

### 6.2 Input Event 示例

Stream：`source=system`，`schema=heartbeat.input/1`，Subject 为 Machine。

```json
{
  "streamId": "0198d5f4-c41b-79f2-8e4a-a99f30d19ecb",
  "schemaRevision": 1,
  "factId": "0198d5f4-c5be-72ac-8122-b41a25901501",
  "revision": 1,
  "recordState": "present",
  "time": {
    "occurredAt": "2026-08-22T12:03:14.225Z"
  },
  "payload": {
    "eventType": "keyDown",
    "codeSet": "heartbeat-key-position-v1",
    "code": 4
  }
}
```

`code=4` 由 `heartbeat-key-position-v1` 解释为 `KeyA`；数值本身不能脱离 CodeSet 解释。这取代“零长度 ActivitySegment 就是通用事件”的绿场方向，但不要求立即迁移现有 InputEvent 表。

## 7. Measurement

Measurement 表示数值状态或时间窗中的数值总体。Measurement 的类型与单位属于 Stream，不在每个 Fact 重复。

### 7.1 Value 与 Missing

有观测值时：

```json
{
  "payload": {
    "status": "value",
    "value": 78
  }
}
```

明确知道缺失时：

```json
{
  "payload": {
    "status": "missing",
    "reason": "sensorOffBody"
  }
}
```

没有 Fact 表示 Collector 没有提交观测；`status: missing` 表示 Collector 明确观测到这段数据不可用。缺失不得用数值 `0`、`null` 或省略 payload 冒充；Histogram 的 count=0 则表示确实观测到窗口内零次样本。

各类型 value payload 的附加字段：

| 类型 | 必须字段 | 条件字段 |
| --- | --- | --- |
| Gauge | `status=value`, `value` | 无 |
| Sum | `status=value`, `value` | cumulative 时必须有 `reset` |
| Histogram | `status=value`, `count`, `bucketCounts` | cumulative 时必须有 `reset`；`sum/min/max` 可选 |

`temporality=delta` 时不得携带 `reset`：delta 值本身已经以窗口为界，重启只体现为窗口不连续。`status=missing` 时不得同时携带 value、count、bucketCounts、sum、min、max 或 reset。

### 7.2 Gauge

Gauge 表示某时刻的数值状态，Stream descriptor 固定 `temporality: instant`。

```json
{
  "streamId": "0198d5f7-60e3-7f24-a2a7-e2285745cb57",
  "schemaRevision": 1,
  "factId": "0198d5f7-62b6-70c3-9dfc-bef0161af8db",
  "revision": 1,
  "observedAt": "2026-08-22T12:10:00Z",
  "recordState": "present",
  "time": {
    "at": "2026-08-22T12:09:58Z"
  },
  "payload": {
    "status": "value",
    "value": 78
  }
}
```

示例 Stream metadata：`source=health.watch`、Subject Person、`type=gauge`、`unit={beat}/min`。

### 7.3 Sum

Sum 表示一个时间窗的增量或从累计起点到窗口终点的累计值。

```json
{
  "streamId": "0198d5fa-5513-7d63-9b47-6e1e8d606e3f",
  "schemaRevision": 1,
  "factId": "0198d5fa-5680-783b-8b05-716fcf777661",
  "revision": 1,
  "recordState": "present",
  "time": {
    "start": "2026-08-22T00:00:00Z",
    "end": "2026-08-22T12:15:00Z"
  },
  "payload": {
    "status": "value",
    "value": 6342,
    "reset": false
  }
}
```

- `delta`：窗口通常连续但不要求相邻；每个 Fact 表示 `(start, end]` 的增量。
- `cumulative`：`start` 是本累计 epoch 起点，后续窗口通常保持 start 不变；源重置时使用新 start 且 `reset: true`。
- `monotonic: true` 时，同一 cumulative epoch 的 value 不得下降；下降必须显式开始新 reset epoch 或作为更高 Revision 纠错。

步数通常使用 `cumulative + monotonic + unit=1`，而不是 Gauge。

### 7.4 Histogram

Histogram 的时间语义与 Sum 相同；Stream metadata 固定 bounds。Value payload：

```json
{
  "status": "value",
  "count": 6,
  "sum": 342,
  "bucketCounts": [1, 2, 2, 1],
  "min": 12,
  "max": 120,
  "reset": false
}
```

若 bounds 为 `[30, 60, 90]`，`bucketCounts` 长度必须为 `bounds.length + 1`。`count` 必须等于所有 bucketCounts 之和；`sum`、`min`、`max` 是可选统计，存在时必须与 count 和 buckets 一致。明确观测到“窗口内零次样本”可以发送 count=0 与全零 buckets；只有数据源不可用时才使用 `status: missing`。

### 7.5 Measurement identity 与修正

一个采样点或聚合窗口首次产生时分配 FactId。重传或历史修正保留 FactId；Measurement 的事实时间属于 point/window identity，同一 FactId 的更高 Revision 不得改变 `at`、`start` 或 `end`，只能修正 value、missing、reset 或其他 payload。cumulative Sum 在新的 end 时刻产生新 FactId，而不是把上一个点当成生长中的 Segment。

相同时间窗不自动表示同一 Fact。Hub 不以 timestamp 代替 FactId 去重，避免两个独立来源或重新计算批次被错误合并。

## 8. Schema 版本

Manifest 中的 Fact Schema 引用固定 identity、locator 与 exact bytes hash：

```json
{
  "id": "heartbeat.browser.tab",
  "major": 1,
  "revision": 2,
  "document": "schemas/heartbeat.browser.tab-v1.2.schema.json",
  "hash": "sha256:..."
}
```

- `id`：稳定语义名称，推荐反向域风格的小写点分段。
- `major`：破坏性语义兼容线；改变 required 字段、字段含义、枚举含义或 identity 规则必须提高。
- `revision`：同一 Major 内只增的兼容扩展，例如增加可选字段。
- `document`：Package root 内的 portable 相对路径；不得包含绝对路径、反斜杠、空段、`.` / `..` 段或符号链接穿越。
- `hash`：`sha256:<64 lowercase hex>`，输入是 `document` 指向文件的完整原始字节。

Fact Schema Document v1 是严格 JSON 对象，固定形状如下：

```json
{
  "documentVersion": 1,
  "schemaId": "heartbeat.browser.tab",
  "schemaMajor": 1,
  "schemaRevision": 2,
  "factKind": "segment",
  "evolution": {
    "mode": "segmentSnapshot",
    "allowRetraction": true
  },
  "payloadSchemaDialect": "https://json-schema.org/draft/2020-12/schema",
  "payloadSchema": {
    "type": "object",
    "additionalProperties": false,
    "required": ["url", "title"],
    "properties": {
      "url": { "type": "string", "minLength": 1 },
      "title": { "type": "string", "minLength": 1 }
    }
  }
}
```

顶层与 `evolution` 都严格拒绝未知字段和重复键。`payloadSchema` 使用 JSON Schema Draft 2020-12，可以是 object 或 boolean schema；v1 必须自包含，只允许 fragment 形式的本地 `$ref` / `$dynamicRef`，且每个引用都必须在 Package 校验阶段解析成功，不得解析网络或 Package 外部引用。若 payload 内嵌 `$schema`，它必须等于 `payloadSchemaDialect`。

`schemaId / schemaMajor / schemaRevision / factKind` 必须逐项等于 Manifest Output 引用。同一个 Package 内，同一 `(schemaId, schemaMajor, schemaRevision)` 只能映射到一个 document 与 hash。`payloadSchema` 只验证 `recordState: present` 的 payload；公共 Fact 信封和 FactKind time/evolution 仍由协议 Runtime 验证。

`evolution.mode` 与 FactKind 的合法组合固定为：

| FactKind | mode | 附加规则 |
| --- | --- | --- |
| Segment | `segmentSnapshot` | `allowRetraction` 必须为 true；每个 Revision 是完整快照 |
| Event | `immutableEvent` | present 内容不可修订；是否可撤回由 `allowRetraction` 决定 |
| Event | `mutableEvent` | 必须增加非空 `mutablePayloadPaths`，每项为 JSON Pointer；只允许这些 payload 路径变化 |
| Measurement | `measurementCorrection` | point/window time 不变，只允许更高 Revision 修正 value/missing/reset 等 payload |

除 `mutableEvent` 外不得出现 `mutablePayloadPaths`。`allowRetraction` 必须显式给出；它与是否允许更高 present Revision 是两条独立规则。

Hash 输入不做 canonicalization：文件必须是有效 UTF-8 且无 BOM，直接对**完整原始文件字节**计算 SHA-256。空白、键顺序、LF/CRLF 与末尾换行都参与 hash。Manifest 与 schema 文档可先解析后比较，但验证器不得重新序列化 JSON 来计算 expected hash。

一个 Stream identity 绑定 `SchemaId + SchemaMajor`，schema catalog 保存该 Major 下每个 SchemaRevision → Hash 的精确映射。每条 FactSubmission 携带实际 SchemaRevision，使新 Activation 可以重发旧 outbox 而不误用当前 schema。Hub 只有在双方协商并持有该精确 schema 时才接受 payload；不得仅凭“JSON 能反序列化”就把语义未知内容当成兼容。

## 9. Canonical 内容与收敛

Hub 对同一 `StreamId + FactId` 按以下顺序处理：

1. 不存在：验证 schema 与 Revision 为正数，提交；Segment/Measurement 可以因本地快照压缩而首次收到大于 1 的 Revision。
2. 已有更低 Revision：验证该 FactKind 的演进规则，合法则替换当前快照并保留审计信息。
3. 已有相同 Revision：canonical 内容相同则 duplicate；不同则 `fact_revision_conflict`。
4. 已有更高 Revision：返回 superseded，不回滚。

Canonical 内容恰好覆盖 `schemaRevision`、`recordState`、`time` 与 `payload` 四项，不多不少；ObservedAt、ingestMetadata 和传输字段不参与内容相等判断。这里必须是精确集合而不是下界，否则两端对“内容相等”的判断会分叉，`duplicate` 与 `fact_revision_conflict` 的边界随实现漂移。

内容相等在 schema 验证后按 JSON 语义比较：object key 顺序不参与相等，array 顺序参与；schema 为 number 时 `78`、`78.0` 与 `7.8e1` 相等，`-0` 与 `0` 相等；string 与 number 永不相等，字符串按 Unicode code point 原值比较。Dimension value 是唯一额外执行 NFC 规范化的字符串。标准信封字段严格拒绝未知字段；config、payload 与 error details 的未知字段/null 只由各自 schema 决定。

## 10. 现有模型映射

| 当前形状 | v1 目标 | 迁移说明 |
| --- | --- | --- |
| `ActivitySegment.Id` | Segment FactId | 可直接沿用 UUIDv7 |
| `Source` | Stream metadata Source | 从逐条 Fact 上移 |
| `IdentityKey` | typed payload/schema identity hint | 不再是所有 Fact 的公共字段 |
| `StartTime/EndTime` | Segment time | 新增 `isFinal` 与显式 Revision |
| `Attributes` | typed payload | 每个 Schema 独立约束，仍可保留高保真内容 |
| zero-length ActivitySegment | Event | 只迁移真实离散事件；不是所有历史零长度段都自动改类 |
| `InputEvent.Id/Timestamp` | Event FactId/occurredAt | 可直接映射 |
| Hub 按 Id 后到覆盖 | Revision 收敛 | legacy adapter 暂留旧规则，新 Collector 必须发送 Revision |
| Device | Machine Subject | Account/Person 首次落地时另做 schema 迁移 |
| `AppHint` → `AppIdentity` 解析 | 未定 | 现在由 hub 平台 adapter 在进入严格缓存前改写采集器内容（ADR-034），而 v1 只允许 Hub 附加 ReceivedAt；富化要么退到 legacy adapter 边界内，要么变成独立派生层 |
| Observation Depth 声明（ADR-030） | 未定 | 声明谓词的 `from` 槽位（`appName`、`title`、`identityKey`、`attributes.<path>`）在 typed payload 下全部消失，Matcher（ADR-029/031）直接建在这些槽位上 |
| Current Activity、presence keepalive（ADR-021） | 不是 Fact | 属于 hub 读模型，v1 三类事实里没有它的位置，需要单独说明它留在协议之外 |

旧 Segment 没有 Revision、StreamId 和 SubjectKind，无法仅靠无状态字段重命名成为完整 v1 Fact。迁移适配器必须明确标记 legacy 来源，不得假装它已经满足新收敛规则。表中标“未定”的三行是 v1 的真实缺口，不是留给实现自由发挥的空间。

## 11. v1 校验矩阵

| 场景 | 结果 |
| --- | --- |
| Segment 同 FactId、start 不变、Revision 增加、end 增长 | 接受 |
| Segment 更高 Revision 改变 start | 拒绝 |
| Segment `isFinal=true` 后回到 false | 拒绝 |
| Event Revision 1 完全相同重放 | duplicate |
| 默认 Event 同 FactId 内容不同 | conflict |
| Gauge 使用区间 time | 拒绝 |
| Measurement 更高 Revision 改变 point/window time | 拒绝 |
| cumulative monotonic Sum 同 epoch 值下降且无 reset/纠错 | 拒绝 |
| Histogram bucketCounts 长度或 count 不匹配 | 拒绝 |
| missing Measurement 同时携带 value | 拒绝 |
| 同 Revision 仅 ObservedAt 或 ingestMetadata 不同 | duplicate |
| 较低 Revision 迟到 | superseded |
| Package 更新但 Stream identity 未变 | 复用 StreamId |
| Subject、SchemaMajor、Measurement descriptor 或 dimensions 改变 | 新建 Stream |
| Event 首次提交 Revision 大于 1 | 拒绝 |
| Segment 或 Measurement 首次提交 Revision 大于 1（本地快照压缩） | 接受 |
| Schema evolution 不是 `mutableEvent` 的 Event 提交更高 present Revision | 拒绝 |
| Schema evolution 未声明 `allowRetraction: true` 的 Event 或 Measurement 提交 retracted | 拒绝 |
| 已 retracted 的 FactId 用更高 Revision 恢复为 present | 拒绝 |
| delta Sum 携带 `reset` | 拒绝 |
| Dimension value 仅 Unicode 规范形式不同 | NFC 后落到同一 Stream |
| Dimension key 不匹配 `^[a-z][a-zA-Z0-9]*$` 或未在 Output Template 声明 | 拒绝 |

## 12. 未决问题

以下问题会改动本规范本身，实现前应先裁决；完整背景见 [PRD 草案暴露的矛盾](../PRD.md#草案暴露的矛盾待裁决)。

| # | 问题 | 影响面 |
| --- | --- | --- |
| 1 | AppHint → AppIdentity 富化与“Hub 只附加 ReceivedAt”的冲突（§10） | 阻塞：现有 system Collector 迁移路径 |
| 2 | Observation Depth 声明在 typed payload 下的取值来源（§10） | 阻塞：Matcher 与知识层 |
| 3 | identifying dimensions 的基数与长度约束及对应拒绝码（§3.2） | 高：无拦截点 |
| 4 | 数值相等的比较精度：`78 == 78.0` 已定，但未说明按 IEEE 754 双精度还是十进制字面量比较（§9） | 中：两端 canonical 判断可能分叉 |
| 5 | Retraction 后 `time` 与 Segment `isFinal` 是否仍受原 FactKind 约束（§4.1） | 低 |
