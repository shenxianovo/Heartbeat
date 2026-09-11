# Collector Protocol Client

Collector 侧协议库。它统一承担 Activation 生命周期、持久 outbox、ACK/重试、Stream Gap、
交互授权、Collector Secret 与 drain；Collector 只提交观测事实。

## 目录

- `CollectorProtocolClient.cs` / `CollectorActivation`：应用入口与 Fact 发布 seam。
- `ICollectorProtocolBinding.cs`：Transport Binding 接口，不拥有协议语义。
- `StdioCollectorProtocolBinding.cs`：ManagedProcess 的 NDJSON stdio binding。
- `CollectorProtocolOutbox.cs`：未确认 Fact、Gap 与 dead-letter 的持久责任。
- `CollectorProtocolModels.cs`：Collector 侧类型化协议模型。

未 ACK 数据保持持久；drain 到期时如实返回剩余项，不伪装成已送达。
Stream Gap 以稳定 UUIDv7 `GapId` 幂等；`messageId` 只关联一次传输尝试。各 Transport Binding
必须原样传递 GapId，Hub 不按相同 range/reason 合并不同的丢失事实。

## 验证与归属

```bash
dotnet test collection/protocol/Heartbeat.Collection.CollectorProtocol.Tests
```

本库不独立部署，随使用它的 Collector 交付。术语见
[Collection Context](../../CONTEXT.md)，决策见 [ADR-040](../../../docs/adr/040-collector-runtime-and-protocol-foundation.md)，
跨语言行为见 [Conformance Suite](../conformance/README.md)。

## 独立观测发布

`CollectorFact` / `BoundCollectorFact` 末尾的 `Kind` 显式选择独立观测契约，支持 `segment`、`event`。
事实填写稳定 `FactId`、`CollectorId`、`Foi`、`Aspect`、单调 `Revision`、家族 `Time`，
`Payload` 原样承载完整 Result；`Source` 可空，`Relations` 默认空列表。
`CollectorSegmentFactTime.IsFinal` 仍是本地终态和准确 ACK 的一部分，不能重开已终结的事实。

没有交付分组时，Definition 使用 `RequiredSubjectKind: null`、`Outputs: []`，Fact 使用
`BindingId: ""`。Binding 收到 `StreamId: Guid.Empty`，stdio 不写 streamId；这不会创建旧 Subject/Stream。
需要 Gap 的 Collector 仍声明自己的交付 Binding，Fact 的 Kind/FOI/身份不从该分组推断。
无分组 outbox 满时施加背压，调用方保留未接纳观测以便重试；不会制造无归属 Gap。

原生事实要求协商 `facts.observation: 2`。v1 Hub 无法确认新事实，SDK 保持其待发状态；
持有独立事实的 outbox/dead-letter 使用 schema 4，旧 SDK 应拒绝未知版本并保留原文件。
SDK 在发布时复制完整快照，拒绝同版本内容冲突与固定字段变更；低版本不覆盖高版本。
不传 Kind 的既有绑定调用方式暂时保留，供尚未迁移生产者排空和逐项切换。
