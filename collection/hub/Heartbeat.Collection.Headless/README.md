# Headless Collection Host

无头 Hub host：一个 Collector Runtime 托管多个 ManagedProcess Collector Instance，每个
Instance 独立拥有投影、状态、上传身份、缓存与 Secret。owner-only 管理 API 位于
`/hub/api/v1`；Dashboard 直连 Hub，Analytics 不代理凭据或授权应答。

## 目录

- `Program.cs`：host、管理 API 与认证组合入口。
- `HeadlessFleetManager.cs`：多 Instance 编排。
- `HeadlessInstancePipelines.cs`：per-Instance 投影、状态、上传、缓存与 drain。
- `HeadlessFleetOptions.cs`：配置校验。
- `heartbeat-headless.compose.example.json`：配置 shape 的权威示例。

## 运行、验证与归属

```bash
dotnet run --project collection/hub/Heartbeat.Collection.Headless -- <config.json>
dotnet test collection/hub/Heartbeat.Collection.Headless.Tests
```

本地 Compose 路径见 [Development Guide](../../../docs/development.md)。本项目构建为
`heartbeat-headless` 镜像，当前内含 [VRChat Collector Package](../../collectors/Heartbeat.Collector.VRChat/README.md)。
交互授权边界见 [ADR-043](../../../docs/adr/043-hub-local-interactive-collector-authorization.md)。
