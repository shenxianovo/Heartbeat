# Collector Protocol v1

Status: Draft 0.2

本规范定义 Collector Activation 与 Hub 之间的统一语义协议。它是 ADR-040 的可实现草案，不定义 Package Registry、安全认证、Analytics ingest 或具体传输帧格式。

本文使用“必须 / 不得 / 应 / 可以”表达规范强度。

## 1. 目标与边界

Collector Protocol v1 必须让以下三种 Collector 使用相同的会话和 Fact 语义：

- `system`：`BuiltIn + InProcess`
- `browser`：当前为 `RuntimeManaged + ExternalHost`
- `vrchat.account`：目标为 `RuntimeManaged + ManagedProcess`

协议负责：

- 协议与能力协商；
- Collector Instance 的完整期望状态下发；
- Output Template 到 Fact Stream 的绑定；
- Activation 就绪、动态配置与停止；
- Fact 批量发布、确认、重试和背压；
- 结构化错误与运行诊断。

协议不负责：

- Package 的发现、下载、签名与信任；
- 第三方权限、Secret 与沙箱；
- Hub → Analytics 的上行协议；
- Fact 在 Analytics 中的物理表与查询 API；
- Collector 内部如何从原始信号折叠出 Fact。

## 2. 身份与状态所有权

| 身份 | 所有者 | 稳定范围 | 是否进入 Fact Stream 身份 |
| --- | --- | --- | --- |
| `packageId + packageVersion` | Package 发布者 | 一个不可变发布版本 | 间接，不作为 Stream identity |
| `collectorInstanceId` | Hub Runtime | 同一 PackageId、Subject 与 Desired State 生命周期，跨 Activation | 是 |
| `activationId` | Hub Runtime | 一次协议会话 | 否，仅 provenance |
| `subjectId` | Owner/Analytics 领域 | 被观测主体生命周期 | 是 |
| `source` | Output Template | 观测者语义兼容线 | 是 |
| `streamId` | Hub Runtime | 一个具体 Output binding | Fact 通过它引用 Stream |
| `factId` | Collector | 一个事实及其全部 Revision | 与 StreamId 共同定位 Fact |

Hub 是 Desired、Resolved、Installed 与 Runtime State 的权威。Collector 可以报告实际状态，但不得用自报状态改写 Hub 的 Desired State。

Collector Instance 在创建时永久绑定一个 `packageId` 和一个 `subjectId + kind`。Activation、配置或 PackageVersion 更新不得改变这两项；要改 PackageId 或 Subject 必须创建新的 Instance。同一 PackageId 的候选 PackageVersion 可以在通过协议、制品、Output 与 schema 兼容检查并到达 Ready 后，原子替换 Instance 当前解析的版本与 fingerprint；失败时保留旧解析结果。同一 PackageVersion 对应不同 fingerprint 违反 Package 不可变性，必须拒绝。

## 3. 协议与能力版本

### 3.1 三条独立版本轴

- `packageVersion`：Collector 发布 SemVer。
- `protocolMajor`：基础会话与消息语义的破坏性版本。
- capability version：某项可选能力自己的整数版本。

三者不得互相推导。Package 更新不要求提高协议版本；新增 capability 也不要求提高基础 `protocolMajor`。

### 3.2 v1 capability 名称

| Capability | 含义 |
| --- | --- |
| `facts.segment` | 发布 Segment |
| `facts.event` | 发布 Event |
| `facts.measurement.gauge` | 发布 Gauge Measurement |
| `facts.measurement.sum` | 发布 Sum Measurement |
| `facts.measurement.histogram` | 发布 Histogram Measurement |
| `config.dynamic` | Ready 后原地应用更高 SpecRevision |
| `diagnostics.stream-gap` | 报告不可恢复的数据缺口 |

双方用 `name -> supported integer versions` 声明 `supportedCapabilities`。Hub 在 `selectedCapabilities` 中为每项能力选择双方交集中的一个版本；未被选择的能力不得在本次 Activation 使用。

能力分**必需**与**可选**两档，无交集时的后果不同：Instance 已声明的 Output 所依赖的 `facts.*` 能力，以及所有产生 Fact 的 Activation 所需的 `diagnostics.stream-gap`，都是必需能力；任一无交集时 Hub 必须以 `capability_no_common_version` 拒绝 Hello。`config.dynamic` 是可选能力，无交集时不进入 `selectedCapabilities`，配置改动改走新 Activation。无法持久披露 Stream Gap 的 Activation 不得进入完整 Fact 交付的 Ready 状态。

## 4. Canonical JSON 消息

所有 Transport Binding 都映射到同一逻辑消息。协商前的 `activation.hello / accepted / rejected` 使用固定的 `heartbeat.collector.bootstrap/1` 信封；选定 Major 后，后续消息使用 `heartbeat.collector/{major}`。JSON 表示必须满足：

- UTF-8；字段名使用 `lowerCamelCase`；
- UUID 使用带连字符的小写 canonical 形式；
- 时间使用带 `Z` 的 RFC 3339 UTC；
- 标准信封和标准消息字段缺席时省略，不用 `null` 代替缺席；`config.value`、Fact `payload` 与 `error.details` 是否允许 null 由各自 schema 决定；
- 不允许重复对象键、`NaN`、`Infinity` 或超出 JSON number 精确表达范围的整数；
- 标准信封和标准消息 body 中，未协商 capability 引入的未知字段必须拒绝，防止拼写错误被静默吞掉；`config.value`、Fact `payload` 与 `error.details` 只按各自 schema 判断未知字段。

逻辑消息信封：

```json
{
  "protocol": "heartbeat.collector/1",
  "type": "activation.health",
  "messageId": "0198d5e8-2b66-7a27-91b8-6524bdca51c5",
  "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c",
  "body": {
    "health": "healthy",
    "observedAt": "2026-08-22T12:04:00Z",
    "diagnostics": []
  }
}
```

约束：

- `protocol`：所有消息必须携带；协商消息固定为 bootstrap/1，协商后固定为选中的 Major。
- `type`：本规范定义的消息类型。
- `messageId`：发送方生成的 UUIDv7，标识一次传输 attempt。因连接中断、超时或响应无法解析而重传同一 attempt 时保持不变；已经明确收到 `retryable=true` 或 Fact `status=retry` 后，下一次 attempt 必须使用新的 messageId。创建新 Activation 后也必须使用新的 messageId，但 FactId、SchemaRevision 与 Fact Revision 保持不变。
- `activationId`：协商完成后的消息必须携带；bootstrap 的 `hello`、`accepted`、`rejected` 不携带在信封中，Accepted 在 body 内分配它。
- `replyTo`：响应消息必须指向请求的 `messageId`；通知消息省略。
- `body`：该消息类型的严格对象。

Transport 可以提供额外的关联机制，但不得改变上述逻辑身份与幂等语义。

## 5. Activation 生命周期

```text
WaitingForExternalHost / Starting
              │
              ▼
          Negotiating ───────────────▶ Failed
              │
              ▼
        OpeningStreams ──────────────▶ Failed
              │
              ▼
             Ready ─────▶ Degraded ──▶ Ready
              │
              ▼
           Draining ────────────────▶ Stopped
```

安全阶段可以在 `Starting` 与 `Negotiating` 之间加入 `Authenticating`，但 v1 不定义认证消息。

### 5.1 `activation.hello`：Collector → Hub

Collector 建立逻辑会话时发送。Hello 的 `messageId` 同时是 Activation attempt 的幂等键：响应丢失后以同一 messageId 重试，Hub 必须返回同一 ActivationId 和相同协商结果；已失效的 attempt 返回稳定错误，不得悄悄创建另一个 Activation。Collector 主动开始新的 attempt 时生成新 messageId，新 attempt 不恢复旧会话。

```json
{
  "protocol": "heartbeat.collector.bootstrap/1",
  "type": "activation.hello",
  "messageId": "0198d5e8-2b66-7a27-91b8-6524bdca51c5",
  "body": {
    "collectorInstanceId": "0198d5e0-5d15-73d8-a6d8-84a50ddf855f",
    "runtimeArtifact": {
      "packageId": "heartbeat.collector.browser",
      "packageVersion": "0.1.0",
      "artifactId": "extension.chromium",
      "artifactHash": "sha256:..."
    },
    "protocolMajors": [1],
    "supportedCapabilities": {
      "facts.segment": [1],
      "config.dynamic": [1],
      "diagnostics.stream-gap": [1]
    }
  }
}
```

`artifactId` 标识 Manifest 中被选中的实际运行制品。`artifactHash` 在 Runtime 能核对实际加载制品时必须提供，并且必须等于该 artifact 的 Manifest `contentHash`；ExternalHost 只能自报时可以省略。这个差异只是运行事实，不在 v1 中做信任判断。Hello 的实际能力必须同时受 Manifest 发布声明约束，最终选择不得超出两者交集。

### 5.2 `activation.accepted`：Hub → Collector

Hub 选择协议与能力并分配 ActivationId。Bootstrap response 不携带任何版本化 Instance、Spec、limits 或 Fact 字段。

```json
{
  "protocol": "heartbeat.collector.bootstrap/1",
  "type": "activation.accepted",
  "messageId": "0198d5e8-30cc-743c-a3d6-ac61956f26b5",
  "replyTo": "0198d5e8-2b66-7a27-91b8-6524bdca51c5",
  "body": {
    "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c",
    "selectedProtocolMajor": 1,
    "selectedCapabilities": {
      "facts.segment": 1,
      "config.dynamic": 1,
      "diagnostics.stream-gap": 1
    }
  }
}
```

若没有基础协议交集、Package 与 Instance 不兼容或当前 Desired State 不允许激活，Hub 使用 bootstrap 信封返回 `activation.rejected`，不分配 ActivationId。固定 bootstrap 只负责协商，不承载配置、Stream 或 Fact，因此未来基础协议 Major 变化不会造成“必须先会说新协议才能协商新协议”的循环依赖。

### 5.3 `activation.initialize`：Hub → Collector

协商完成后，Hub 在选中的协议 Major 下发送 Instance context、完整 Spec snapshot 与流控限制：

```json
{
  "protocol": "heartbeat.collector/1",
  "type": "activation.initialize",
  "messageId": "0198d5e8-3266-78d8-8d9c-8f87af141aca",
  "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c",
  "body": {
    "instance": {
      "collectorInstanceId": "0198d5e0-5d15-73d8-a6d8-84a50ddf855f",
      "subject": {
        "subjectId": "0198d5df-5df3-70a1-937d-68a7d64623e2",
        "kind": "machine"
      }
    },
    "spec": {
      "revision": 7,
      "config": {
        "schemaVersion": 1,
        "value": {
          "flushPeriodMs": 30000
        }
      }
    },
    "limits": {
      "maxFactsPerBatch": 500,
      "maxBatchBytes": 1048576,
      "maxInFlightBatches": 2
    },
    "hubTime": "2026-08-22T12:00:00Z"
  }
}
```

Collector 成功验证并应用 snapshot 后返回 `activation.initialized { appliedSpecRevision }`；无法应用时返回 `activation.initializeRejected { error }`。Instance 和 Subject 是本次 Activation 的稳定 context，不属于可动态修改的 Spec。Disabled Instance 不会收到 initialize，而是在 bootstrap 阶段被拒绝或对现有 Activation 执行 drain。

### 5.4 `streams.open`：Collector → Hub

Collector 只能实例化 Package Manifest 中的 Output Template。一个模板可以按允许的 identifying dimensions 打开一个或多个具体 Stream。

```json
{
  "protocol": "heartbeat.collector/1",
  "type": "streams.open",
  "messageId": "0198d5e8-35cb-73ee-aa8b-876bf6aab600",
  "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c",
  "body": {
    "specRevision": 7,
    "bindings": [
      {
        "bindingId": "tabs",
        "outputId": "tabs",
        "dimensions": {}
      }
    ]
  }
}
```

`bindingId` 只在当前 Activation 内供 Collector 关联响应，不参与持久身份。Dimension value 在 v1 中只允许非 null JSON string；Hub 对 value 做 Unicode NFC 规范化、按 key ordinal 排序后计算 identity。JSON number `1` 必须被拒绝，不能隐式转换成 string `"1"`；不同 Unicode 规范形式统一为 NFC 后比较。Hub 以 Instance、Subject、Output Template、Schema Major、Measurement descriptor 与 canonical dimensions 查找或创建稳定 Stream。

### 5.5 `streams.opened`：Hub → Collector

```json
{
  "protocol": "heartbeat.collector/1",
  "type": "streams.opened",
  "messageId": "0198d5e8-36a9-76a4-b707-5d180e7d4ae3",
  "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c",
  "replyTo": "0198d5e8-35cb-73ee-aa8b-876bf6aab600",
  "body": {
    "streams": [
      {
        "bindingId": "tabs",
        "stream": {
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
      }
    ]
  }
}
```

`streams.opened` 返回完整 StreamDescriptor；Measurement Stream 还必须包含 Manifest 声明的 `measurement` descriptor。Descriptor 中的 `schema.revision/hash` 是新 Fact 默认使用的当前 revision，不属于 Stream identity；重放旧 outbox 时每条 Fact 仍声明其实际 `schemaRevision`。Hub 同时只把一个 Activation 设为某 Stream 的 writer。旧 Activation 或未获 writer lease 的会话发布到该 Stream 时，必须被拒绝为 `stream_writer_conflict`。

v1 固定采用 stop-first writer 语义：Runtime 必须先停止旧 Activation 并释放全部 writer lease，之后才允许 replacement Activation 开始 initialize/open；不得预建两个同时存活的候选再抢占 lease。旧 Activation 停止后的迟到发布以 `stream_writer_conflict` 拒绝。更新失败时重新启动旧 Package 也是一次新的 Activation，并复用原 StreamId。

`streams.open` 按请求原子处理：Hub 必须先验证全部 binding；任一个 binding 不合法时返回 `streams.rejected`，且不得创建或授予任何 Stream writer。成功时 `streams.opened.streams` 必须与请求 bindings 一一对应。

### 5.6 `activation.ready`：Collector → Hub

Collector 成功应用当前 SpecRevision、取得所有必需 Stream，并准备承担采集责任后发送：

```json
{
  "protocol": "heartbeat.collector/1",
  "type": "activation.ready",
  "messageId": "0198d5e8-3a92-78b2-b29e-fae10df6f89e",
  "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c",
  "body": {
    "appliedSpecRevision": 7
  }
}
```

Hub 返回 `activation.readyAck` 后双方才进入 Ready。Ready 不要求已经产生第一条 Fact；候选 Package 更新只有在 Hub 确认 Ready 后才提交为当前版本。

## 6. Desired State 更新

### 6.1 `spec.apply`：Hub → Collector

只在协商 `config.dynamic` 后使用。消息携带完整快照，不发送 JSON Patch：

```json
{
  "protocol": "heartbeat.collector/1",
  "type": "spec.apply",
  "messageId": "0198d5eb-e888-73ee-aae8-0aa7ca993d95",
  "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c",
  "body": {
    "spec": {
      "revision": 8,
      "config": {
        "schemaVersion": 1,
        "value": {
          "flushPeriodMs": 60000
        }
      }
    }
  }
}
```

Collector 原子应用后返回 `spec.applied { appliedSpecRevision }`；无法应用时返回 `spec.rejected`，包含结构化原因。Hub 不因拒绝而改写 Desired State，可以选择保持旧 Revision 或重建 Activation。

不支持 `config.dynamic` 时，任何会改变运行行为的 SpecRevision 都通过新 Activation 收敛。即使支持动态配置，Package、Subject、Output binding 或 enabled 状态的变化仍必须走 Activation 切换或 drain；`spec.apply` v1 只用于同一配置 schema 内的运行参数完整快照。

## 7. Fact 发布与 ACK

### 7.1 `facts.publish`：Collector → Hub

```json
{
  "protocol": "heartbeat.collector/1",
  "type": "facts.publish",
  "messageId": "0198d5ec-04f4-73ab-9785-c13bef872f91",
  "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c",
  "body": {
    "facts": [
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
          "title": "Example Docs"
        }
      }
    ]
  }
}
```

因连接中断、超时或响应无法解析而重传同一次 publish attempt 时，`messageId` 与各 Fact 身份都保持不变。明确收到 `status=retry` 后，该 attempt 已结束；Collector 只把待重试 Fact 放入使用新 messageId 的 publish attempt。创建新 Activation 或重新分批时也使用新的 messageId，但任何情形都不得改变 FactId、SchemaRevision 或 Fact Revision。`messageId/replyTo` 已完整承担批量请求关联，v1 不再定义独立批次身份字段。

### 7.2 `facts.ack`：Hub → Collector

Hub 对每个输入 Fact 返回确定结果：

```json
{
  "protocol": "heartbeat.collector/1",
  "type": "facts.ack",
  "messageId": "0198d5ec-07db-7cb6-8850-f734b06ca53d",
  "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c",
  "replyTo": "0198d5ec-04f4-73ab-9785-c13bef872f91",
  "body": {
    "results": [
      {
        "index": 0,
        "status": "committed"
      }
    ]
  }
}
```

结果语义：

| `status` | Hub 语义 | Collector 行为 |
| --- | --- | --- |
| `committed` | Hub 已取得该 Revision 的持久化责任 | 从 outbox 删除 |
| `duplicate` | 相同 Revision 与内容已持久化 | 从 outbox 删除 |
| `superseded` | Hub 已持有更高 Revision | 从 outbox 删除并记录诊断 |
| `rejected` | 永久违反 schema、Stream 或收敛规则 | 不重试；进入 Collector 诊断/dead letter |
| `retry` | Hub 未取得持久化责任 | 保留；等待 `retryAfterMs` 后用新 messageId 发起新 attempt |

只有 `committed`、`duplicate` 与 `superseded` 是 ACK。连接中断或响应无法解析时，Collector 必须假设未 ACK 并重试。

“取得持久化责任”至少意味着该 Fact Revision 及其收敛元数据已经进入 Hub 可在进程重启后恢复的 durable inbox；写入临时内存缓冲或仅调用现有 Segment sink 不足以返回 ACK。Hub 必须先原子提交 inbox，再投影到现有缓冲；投影失败时保留 durable inbox，并在恢复时重放。一个 state file 同时只能有一个 Runtime owner，避免两个 writer 以整文件替换互相覆盖已 ACK 的状态。

协议 Fact identity 是 `StreamId + FactId`。投影到仅以单个 Guid 键控的 legacy Segment 缓冲时，adapter 必须从这两个值派生稳定、带命名空间的投影 ID；直接使用裸 FactId 会让两个合法 Stream 相互覆盖或撤回。

每个 result 是严格 tagged union：

- `committed`、`duplicate`、`superseded`：只允许 `index` 与 `status`；
- `rejected`：必须增加 `error`，且 `error.retryable=false`；
- `retry`：必须增加 `retryAfterMs` 与 `error`，且 `error.retryable=true`。

`results` 的长度必须等于输入 `facts` 的长度，`index` 必须从 `0` 到 `facts.length - 1` 各出现一次。Hub 在开始逐 Fact 处理前发现消息信封、body 或整体批次限制非法时返回 `facts.rejected { error }`；一旦开始处理，就必须返回 `facts.ack` 并为每个输入 Fact 给出结果，不得用消息级失败掩盖部分提交。

```json
{
  "index": 0,
  "status": "retry",
  "retryAfterMs": 1000,
  "error": {
    "code": "hub_backpressure",
    "message": "Hub is applying backpressure.",
    "retryable": true,
    "details": {}
  }
}
```

Collector 不得超过 `maxFactsPerBatch`、`maxBatchBytes` 与 `maxInFlightBatches`。Hub 可以通过 `retry` 和 `retryAfterMs` 降低发送速率，不需要把传输断开伪装成背压。

## 8. 诊断与停止

### 8.1 `activation.health`

Collector 可以发送不要求响应的健康通知，body 固定为：

```json
{
  "health": "degraded",
  "observedAt": "2026-08-22T12:04:00Z",
  "diagnostics": [
    {
      "code": "source_rate_limited",
      "message": "The upstream source is rate limiting requests."
    }
  ]
}
```

`health` 只能是 `healthy | degraded`；`diagnostics` 必须存在，healthy 时通常为空。诊断 `code` 是 Collector 自己命名的稳定机器码，`message` 仅供人阅读。Health 是 Collector 的观察输入；Ready、Degraded 等 Runtime 生命周期状态仍由 Hub 推导和持有。Health 不修改 Desired State，也不等于 Source 最近产生了 Fact。

### 8.2 `stream.gap`

所有产生 Fact 的 v1 Activation 都必须协商 `diagnostics.stream-gap`。Collector 确认某个 Stream 存在不可恢复的数据缺口时发送：

```json
{
  "protocol": "heartbeat.collector/1",
  "type": "stream.gap",
  "messageId": "0198d5ed-5322-7ff9-a783-322190b14999",
  "activationId": "0198d5e8-30cb-7d54-bab1-250087147e4c",
  "body": {
    "streamId": "0198d5e2-e0d4-7b30-9da7-342ee261bf62",
    "factTime": {
      "start": "2026-08-22T10:00:00Z",
      "end": "2026-08-22T10:05:00Z"
    },
    "reason": "outbox_overflow",
    "estimatedFactsLost": 12
  }
}
```

Hub 以 `stream.gapAck` 确认已经持久接受该诊断；未 ACK 时 Collector 可以重试同一 messageId。Gap 是协议诊断，不是第四种 Fact，也不能伪造成零值 Measurement 或空 Segment。

### 8.3 `activation.drain` / `activation.drained`

Hub 请求停止时发送 `activation.drain { deadline }`。Collector 接受合法请求后立即停止产生新 Fact，尽力提交已有 outbox，并返回 `activation.drained { appliedSpecRevision, pendingFacts, pendingGaps }`；三项均必须存在，计数为非负整数。合法 drain 不可拒绝；body 非法时返回 `activation.drainRejected { error }`。deadline 到期后 Runtime 可以终止 ManagedProcess；ExternalHost 只能被标记为 Stopped/Waiting，不能虚假声称已杀死外部宿主。

## 9. 错误模型

拒绝类响应的 body 固定包含一个 `error` 字段：

```json
{
  "error": {
    "code": "protocol_no_common_major",
    "message": "No common Collector Protocol major.",
    "retryable": false,
    "details": {
      "hubMajors": [1],
      "collectorMajors": [2]
    }
  }
}
```

`activation.rejected`、`activation.initializeRejected`、`spec.rejected` 以及其他失败响应都使用这一 body shape。成功响应使用自己的严格 body，不同时携带 error。`error.details` 由具体 error code 定义，可以包含 null 或扩展字段。

### 9.1 请求/响应代数

每个请求只能得到下表中的一种响应；所有响应都必须携带 `replyTo`。表中的 `{ error }` 指本节固定失败 body，不代表可把 error 混入成功 body。

| 请求 | 成功响应 body | 失败响应 body |
| --- | --- | --- |
| `activation.hello` | `activation.accepted { activationId, selectedProtocolMajor, selectedCapabilities }` | `activation.rejected { error }` |
| `activation.initialize` | `activation.initialized { appliedSpecRevision }` | `activation.initializeRejected { error }` |
| `streams.open` | `streams.opened { streams }` | `streams.rejected { error }` |
| `activation.ready` | `activation.readyAck { appliedSpecRevision }` | `activation.readyRejected { error }` |
| `spec.apply` | `spec.applied { appliedSpecRevision }` | `spec.rejected { error }` |
| `facts.publish` | `facts.ack { results }` | `facts.rejected { error }`，仅允许在处理任何 Fact 前返回 |
| `stream.gap` | `stream.gapAck { streamId }` | `stream.gapRejected { error }` |
| `activation.drain` | `activation.drained { appliedSpecRevision, pendingFacts, pendingGaps }` | `activation.drainRejected { error }`，仅用于非法请求 |

`activation.health` 是单向通知，不进入请求/响应代数。`activation.readyAck.appliedSpecRevision` 必须等于请求值；`stream.gapAck.streamId` 必须等于请求值。因响应未知而重传同一 attempt 时，响应方收到相同 `messageId` 必须返回相同响应语义，不得重复执行已经完成的副作用；发送方已经收到 retryable 响应后必须用新 messageId 开始下一 attempt。

v1 至少固定以下稳定代码：

- `protocol_invalid_message`
- `protocol_no_common_major`
- `capability_no_common_version`
- `instance_not_found`
- `package_mismatch`
- `spec_revision_stale`
- `config_schema_unsupported`
- `output_not_declared`
- `stream_writer_conflict`
- `fact_schema_invalid`
- `fact_revision_conflict`
- `batch_limit_exceeded`
- `hub_backpressure`
- `activation_stopping`

可读 `message` 不参与程序判断；程序只依赖 `code`、`retryable` 与结构化 `details`。

## 10. Manifest 的协议相关最小面

Manifest 不是 Activation 消息，但它约束 Hub 可以接受的握手和 Stream：

```json
{
  "manifestVersion": 1,
  "packageId": "heartbeat.collector.browser",
  "version": "0.1.0",
  "protocolMajors": [1],
  "supportedCapabilities": {
    "facts.segment": [1],
    "config.dynamic": [1],
    "diagnostics.stream-gap": [1]
  },
  "config": {
    "schema": {
      "id": "heartbeat.collector.browser.config",
      "version": 1,
      "hash": "sha256:..."
    },
    "accepts": [1]
  },
  "outputs": [
    {
      "outputId": "tabs",
      "source": "browser",
      "factKind": "segment",
      "schema": {
        "id": "heartbeat.browser.tab",
        "major": 1,
        "revision": 1,
        "document": "schemas/heartbeat.browser.tab-v1.1.schema.json",
        "hash": "sha256:..."
      },
      "subjectKinds": ["machine"],
      "dimensionKeys": []
    }
  ],
  "artifacts": [
    {
      "artifactId": "extension.chromium",
      "selector": {
        "driver": "externalHost",
        "os": ["windows", "macos", "linux"],
        "arch": ["x64", "arm64"]
      },
      "entrypoint": "extension/manifest.json",
      "size": 123456,
      "contentHash": "sha256:..."
    }
  ]
}
```

Manifest 的 `supportedCapabilities` 是发布声明，Hello 的同名字段是实际进程报告；Hub 选择的能力必须同时位于 Manifest、Hello 与 Hub 支持集的交集。一个 output 使用的 Fact capability 必须同时出现在 Manifest 声明中，不允许只写 output 而省略能力版本。

Fact Schema 引用中的 `document` 必须是 package root 内的 portable 相对路径，不允许绝对路径、反斜杠、`.` / `..` 段或符号链接穿越。`hash` 是该文档 UTF-8、无 BOM、未经任何规范化的**完整原始字节**之 SHA-256；空白、对象键顺序、换行风格或末尾换行变化都会改变 hash。文档的 `schemaId / schemaMajor / schemaRevision / factKind` 必须与 Output 引用一致；同一 schema identity 在一个 Package 内只能解析到同一 document 与 hash。文档格式见 [Fact Model v1 §8](./fact-model-v1.md#8-schema-版本)。

Artifact 的 `entrypoint` 使用同样的安全相对路径规则。Runtime 必须先读取不可变快照，并同时核对精确 `size` 与 `contentHash`，之后才可交给 Binding。针对当前 `driver + os + arch` 必须恰好命中一个 Artifact，零个或多个都拒绝。参考本地实现以 Manifest 原始字节的 SHA-256 作为已验证 Package 内容集合的 fingerprint；因为 Manifest 固定所有 Artifact 与 Fact Schema hash，这能检测本地内容变化，但不替代签名或外部信任根。

`dimensionKeys` 只声明允许的 key；v1 的绑定值统一为 NFC 规范化后的非 null string，不支持 number、boolean、array 或 object。需要其他类型时通过新的 capability/version 扩展，不能在 v1 中凭 JSON 外形猜测。

`factKind: measurement` 的 output 必须额外声明完整 descriptor，例如：

```json
{
  "outputId": "heartRate",
  "source": "health.watch",
  "factKind": "measurement",
  "schema": {
    "id": "heartbeat.health.heart-rate",
    "major": 1,
    "revision": 1,
    "document": "schemas/heartbeat.health.heart-rate-v1.1.schema.json",
    "hash": "sha256:..."
  },
  "subjectKinds": ["person"],
  "dimensionKeys": [],
  "measurement": {
    "type": "gauge",
    "unit": "{beat}/min",
    "temporality": "instant"
  }
}
```

同一 `outputId` 在 Package 的一个 Major 兼容线内不得改变 Source、FactKind、Schema Major 或 Measurement descriptor。需要改变时创建新 Stream；破坏性 schema 变化同时提高 Schema Major。

## 11. Transport Binding 要求

| Binding | 允许的承载 | 不得改变的语义 |
| --- | --- | --- |
| InProcess | 类型化接口/Channel | 消息顺序、SpecRevision、Stream 与 ACK 语义 |
| ManagedProcess | stdio、named pipe 或 Unix socket | 进程启动不等于 Ready；断连结束 Activation |
| ExternalHost | loopback HTTP 或未来浏览器 native messaging | 外部宿主自报不变成 Runtime 持有的安装事实 |

每个 Binding 必须通过同一组 transcript contract tests。测试输入是逻辑消息序列，断言状态转换、响应码、ACK 和错误码，而不是具体 HTTP route 或 .NET interface。

## 12. 现有协议迁移

当前接口：

- `GET /v1/hub`
- `GET /v1/collectors/{source}/config`
- `POST /v1/collectors/{source}/declaration`
- `POST /v1/segments`

它们共同承担发现、配置、声明和发布，但没有 InstanceId、ActivationId、SpecRevision、StreamId、Fact Revision 或能力协商，因此不是 Collector Protocol v1 的一种完整 Binding。

迁移期由 Hub 提供 legacy adapter：

- 每个旧 Source 映射到一个默认 Collector Instance；
- config pull 映射到当前 Spec snapshot；
- declaration 映射到 Package Output Template 的临时来源；
- 旧 Segment 经专用 legacy ingest 保持 ADR-018 语义，直到对应 Collector 原生发送 Fact Revision；
- `system` 的现有 `ISegmentSink` 同样通过 InProcess adapter 进入新协议边界。

适配层不得让新协议重新依赖 Source 即实例身份，也不得对外宣称旧 Collector 已完成完整 Activation 握手。

## 13. v1 契约测试最小集合

1. system、browser 与 ManagedProcess 使用不同 Binding 跑过相同 happy-path transcript。
2. bootstrap Accepted 只协商版本和能力；Instance、Spec 与 limits 只能在选定 Major 下由 initialize 发送。
3. 同一 Hello messageId 重试返回同一 ActivationId；新的 attempt 不恢复旧 Activation。
4. 无 protocol Major 交集时在创建 Stream 前结构化拒绝。
5. 未声明 output、SubjectKind、dimension key，或 dimension value 不是 string 时原子拒绝 `streams.open`。
6. Ready 不依赖首条 Fact。
7. 同 Fact 重试得到 `duplicate`；旧 Revision 得到 `superseded`；同 Revision 不同内容得到 `rejected/fact_revision_conflict`。
8. ACK 丢失时以同一 messageId 重传并重放结果，不产生重复事实。
9. 明确收到混合 `committed/retry` 结果后，只用新 messageId 重试未提交 Fact，已提交项不再发送。
10. 新 Activation 可以用原 FactId、Fact Revision 与旧 SchemaRevision 重发旧 outbox。
11. 超过静态批次 shape/count 限制得到非 retryable 的消息级拒绝；超过 in-flight 限制或 durable inbox 暂无容量得到逐 Fact `retry/hub_backpressure`，不静默丢弃。
12. Desired State 修改不会被 Activation 的失败或旧 SpecRevision 覆盖。
13. 旧 Activation 必须先停止并释放 writer；replacement 才能 initialize/open 并复用 StreamId，旧 Activation 的迟到发布被拒绝。
14. ExternalHost 断连改变 Runtime State，但不伪造“浏览器已停止”或“制品已卸载”。

## 14. 代表场景走查

| 场景 | Instance / Subject | Output Streams | Binding |
| --- | --- | --- | --- |
| system 桌面采集 | 一个 system Instance / Machine | foreground Segment、Input Event | InProcess |
| browser 扩展 | 一个 browser Instance / Machine | active-tab Segment | ExternalHost loopback |
| VRChat 常驻采集 | 每个账号一个 Instance / Account | presence Segment | ManagedProcess |
| 心率采集 | 一个 health Collector Instance / Person | heart-rate Gauge | ManagedProcess 或 ExternalHost |
| 微信步数 | 一个微信 Collector Instance / Person | steps cumulative monotonic Sum | ManagedProcess |

同一个无头 Hub 可以同时承载 VRChat Account、心率 Person 与微信步数 Person 的多个 Instance；它们共享协议和运行时，但不共享 Subject、Stream 或 Collector 依赖。
