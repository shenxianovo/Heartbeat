# Collection Hub

Desktop 与 Headless 共用的纯 .NET Collector Runtime。它负责 Package、Instance、Activation、
协议接入、Fact 保管、上传、缓存与 presence，不依赖 UI、平台 API 或发布供应商。

## 目录

- `Hosting/HubServiceCollectionExtensions.cs`：`AddHeartbeatHub` 组合入口，只注册通用运行时。
- `Collectors/Packaging/`：Package、manifest 与 artifact 验证。
- `Collectors/Runtime/`、`Collectors/Protocol/`：运行状态、Execution Driver 与 Hub 侧协议。
- `Segments/`：当前 Segment sink；Fact projector 位于对应 Runtime 模块。
- `Upload/`、`Storage/`：出网上传、离线缓存、dead-letter 与迁移。
- `Auth/`、`Presence/`、`Runtime/`：Analytics 鉴权、当前状态与 hosted workers。

## 验证与归属

```bash
dotnet test collection/hub/Heartbeat.Collection.Hub.Tests
```

本库嵌入 Desktop 与 Headless，不独立部署。当前拓扑见
[系统架构](../../../docs/architecture/system-overview.md)，Fact payload 见
[Contracts](../../contracts/README.md)，跨语言行为见 [Conformance Suite](../../protocol/conformance/README.md)。

## 独立观测入口

原生 Package 声明 `facts.observation: [2]`，可以使用 `outputs: []`，
`defaultInstance` 只填写 `configVersion` 和 `config`，不填写 `subjectKind`。
宿主调用 `CreateInstance(package, spec, instanceKey)` 创建运行和交付保管的 Instance；
为保留旧调用方的源代码兼容，`CollectorInstance.Subject` 的 default 值（空 SubjectId）明确表示没有旧 Subject，
协议初始化将它编码为 `subject: null`，不会建立替代的机器身份。

InProcess 使用 `activation.PublishAsync(messageId, facts)`，ExternalHost 和 ManagedProcess 的
`facts.publish` 省略 `streamId`。原生 Fact 显式填写 Kind、具体 Observer 的稳定 CollectorId（不必等于 Runtime InstanceId）、Foi、Aspect 与 Result，
不能借旧 ObserverId/Target 补足必填信息。初始化仍交换空 `streams.open` 后进入 Ready；
需要 Gap 的生产者继续打开交付分组，分组不决定观测身份。

ManagedProcess 启动及回退前检查 SDK 缓存：schema 4 要求包支持 `facts.observation: 2`，
不匹配时保留原缓存并报告 `collector_cache_incompatible`。`Kind: null` 明确选择旧输入适配，
尚未迁移的旧输出、Subject 初始化和 Stream 发布入口继续有效；旧数据、缓存接管及退出验证由本轮 Ticket 03 承接。

Runtime 在 journal 内以 `DeliveryInstanceId` 保存运行实例的交付保管归属；它不进入 HTTP observation，
不决定 Fact Id、CollectorId、FOI 或 Kind。一个 Instance 可保管不同 External Hosts 的独立 Observer 事实。
