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

## 验证与归属

```bash
dotnet test collection/protocol/Heartbeat.Collection.CollectorProtocol.Tests
```

本库不独立部署，随使用它的 Collector 交付。术语见
[Collection Context](../../CONTEXT.md)，决策见 [ADR-040](../../../docs/adr/040-collector-runtime-and-protocol-foundation.md)，
跨语言行为见 [Conformance Suite](../conformance/README.md)。
