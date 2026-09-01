# Headless Collection Host

无头 Hub host：一个 Collector Runtime 托管多个 ManagedProcess Collector Instance，每个
Instance 独立拥有投影、状态、上传身份、缓存与 Secret。owner-only 管理 API 位于
`/hub/api/v1`；Dashboard 直连 Hub，Analytics 不代理凭据或授权应答。

## 目录

- `Program.cs`：host、认证与依赖组合入口。
- `HeadlessManagementApi.cs`：owner-only 管理 API 的唯一 route group。
- `HeadlessFleetManager.cs`：多 Instance 编排。
- `HeadlessInstancePipelines.cs`：per-Instance 投影、状态、上传、缓存与 drain。
- `HeadlessFleetOptions.cs`：配置校验。
- `heartbeat-headless.compose.example.json`：配置 shape 的权威示例。

## 管理 API

全部 endpoint 都映射在同一个 `RequireAuthorization()` route group 内，未认证请求一律拒绝。

| Endpoint | 作用 |
| --- | --- |
| `GET /hub/api/v1/subjects` | 各 Instance 的 subject、阶段与交互授权挑战。 |
| `POST /hub/api/v1/collector-instances/{id}/authorization/{interactionId}` | 提交交互授权应答。 |
| `GET /hub/api/v1/collector-instances/{id}/package-update` | 当前 Collector Package 更新状态投影。 |
| `POST /hub/api/v1/collector-instances/{id}/package-update/check` | 手动执行一次检查：读 Registry index、下载校验并安装精确候选。 |
| `POST /hub/api/v1/collector-instances/{id}/package-update/approval` | 批准 `{ packageId, version, artifactSha256 }` 精确候选。 |
| `POST /hub/api/v1/collector-instances/{id}/package-update/switch` | 显式开始使用已批准候选；Ready 才算更新成功。 |

手动检查是同步的一次尝试：失败不重试、不排期，以结构化 `lastFailure` 返回，并保留既有 Installation 与
Last-Known-Good。批准只接受仍是本机真实 Collector Installation 的精确候选，不重新解析 latest，也不代表
Ready。`registryBaseUri` 缺省时检查返回 `RegistryNotConfigured`，已安装候选仍可批准。

Collector Package 更新共四个 owner 动作：读 Current、手动 check、approve exact ref、显式 switch。批准不隐含
接管，切换是另一次显式调用，也不能用「重启 host」代替：宿主重启只启动已经到达过 Ready 的那份 Package
（[ADR-047](../../../docs/adr/047-lean-development-collector-web-delivery.md)）。

## 运行、验证与归属

```bash
dotnet run --project collection/hub/Heartbeat.Collection.Headless -- <config.json>
dotnet test collection/hub/Heartbeat.Collection.Headless.Tests
```

本地 Compose 路径见 [Development Guide](../../../docs/development.md)。本项目构建为
`heartbeat-headless` 镜像，当前内含 [VRChat Collector Package](../../collectors/Heartbeat.Collector.VRChat/README.md)。
交互授权边界见 [ADR-043](../../../docs/adr/043-hub-local-interactive-collector-authorization.md)。
