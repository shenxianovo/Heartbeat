# Collection Hub

Desktop 与 Headless 共用的纯 .NET Collector Runtime。它负责 Package、Instance、Activation、
协议接入、Fact 投影、上传、缓存与 presence，不依赖 UI、平台 API 或发布供应商。

## 目录

- `Hosting/HubServiceCollectionExtensions.cs`：`AddHeartbeatHub` 组合入口，只注册通用运行时。
- `Collectors/Packaging/`：Package、manifest、schema 与 artifact 验证。
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
