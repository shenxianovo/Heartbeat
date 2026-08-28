# VRChat Account Collector

实验性的 `vrchat.account` ManagedProcess Collector。它观察 Account Subject 的 VRChat
presence Segment，不是 VRChat 官方集成。

## 目录

- `Program.cs`：stdio 协议入口；`--create-package <dir>` 生成 Collector Package。
- `VRChatManagedCollector.cs`：Activation、授权、轮询与 drain。
- `VRChatApi.cs`：真实 API 与离线 mock adapter。
- `PresenceStateMachine.cs`：presence Segment 的 FactId/Revision 状态机。
- `VRChatPresenceCheckpoint.cs`：跨重启恢复与 Gap。
- `VRChatPackageBuilder.cs`：manifest、schema 与 artifact staging。

## 验证与当前交付

```bash
dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests
```

当前 Package 随 Headless image 交付，不是独立服务；Package 托管与下载尚未实现。运行宿主见
[Headless README](../../hub/Heartbeat.Collection.Headless/README.md)，授权边界见
[ADR-043](../../../docs/adr/043-hub-local-interactive-collector-authorization.md)。
