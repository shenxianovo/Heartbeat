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

## Collector Package 从哪来

`heartbeat-headless` 镜像里不含任何 Collector Package，Package 与 Hub 各自构建、各自发版。
VRChat Package 单独构建到宿主目录：

```bash
./scripts/build-vrchat-package.sh              # 产物默认落在 .local/collector-packages/vrchat
```

```powershell
./scripts/build-vrchat-package.ps1
```

Package 必须在 linux 容器里构建：manifest 的 artifact selector 按构建进程的 OS/arch 写，
在 macOS/Windows 上直接跑 `--create-package` 会得到 Hub 容器用不了的 Package。

Compose 把宿主的 Package 根目录（`HEADLESS_PACKAGE_SOURCE_PATH`，默认
`./.local/collector-packages`）以**只读**方式挂到容器的 `/package-source`，配置里
`packageDirectory` 写 `/package-source/vrchat`。这个目录只是来源：Hub 启动时把它安装成
`/data/collector-packages` 下的一份 Installation，之后一切运行都发生在 Installation 上，
来源目录不会被写入。

换一版 Package 只要重新构建 Package 目录、重启 headless 即可，不用重建 Hub 镜像。已有
`.local/heartbeat-headless.json` 的 owner 需要自己把 `packageDirectory` 从旧的
`/app/vrchat-package` 改成 `/package-source/vrchat`，否则 Hub 会因为找不到 Package 启动失败。

## 运行、验证与归属

```bash
dotnet run --project collection/hub/Heartbeat.Collection.Headless -- <config.json>
dotnet test collection/hub/Heartbeat.Collection.Headless.Tests
```

本地 Compose 路径见 [Development Guide](../../../docs/development.md)。本项目构建为
`heartbeat-headless` 镜像；[VRChat Collector Package](../../collectors/Heartbeat.Collector.VRChat/README.md)
是独立制品，不进镜像。交互授权边界见
[ADR-043](../../../docs/adr/043-hub-local-interactive-collector-authorization.md)，交付单元划分见
[ADR-048](../../../docs/adr/048-shared-collector-host-runtime-and-independent-release-units.md)。
