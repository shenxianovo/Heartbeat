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
- `Dockerfile`：把 Package 构建到宿主目录的入口（构建上下文是仓库根）。

## 构建 Collector Package

```bash
./scripts/build-vrchat-package.sh              # 默认输出 .local/collector-packages/vrchat
./scripts/build-vrchat-package.sh --output /srv/heartbeat/collector-packages/vrchat
```

```powershell
./scripts/build-vrchat-package.ps1
```

两个脚本都会先清空输出目录，再用 BuildKit 在 linux 容器里 publish 并跑
`--create-package`，最后 `--output type=local` 把 Package 导到宿主。必须走容器：manifest 里的
artifact selector 取的是构建进程的 OS/arch，在 macOS/Windows 上直接构建会得到 Headless 容器
选不中的 artifact。

## 验证与当前交付

```bash
dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests
```

Package 是独立于 Headless 镜像的制品：宿主上构建好后，由 Headless 以只读方式挂载、安装再运行；
换 Package 不需要重建 Hub 镜像。Web 托管与下载尚未实现，目前只能本地构建或手工拷贝。运行宿主见
[Headless README](../../hub/Heartbeat.Collection.Headless/README.md)，授权边界见
[ADR-043](../../../docs/adr/043-hub-local-interactive-collector-authorization.md)。
