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

## 本地导出 Collector Package（按需）

日常启动本地栈不需要先执行这些脚本；Headless Hub 会从 Registry 安装已发布 Package。只有修改
VRChat Collector 的打包逻辑、希望在打 tag 前检查 Linux Package 内容时，才需要本地导出：

```bash
./scripts/build-vrchat-package.sh              # 默认输出 .local/collector-packages/vrchat
./scripts/build-vrchat-package.sh --output /srv/heartbeat/collector-packages/vrchat
```

```powershell
./scripts/build-vrchat-package.ps1
```

两个脚本都会先在输出目录的临时 sibling 中用 BuildKit publish 并跑 `--create-package`，验证成功后才替换
带工具 ownership marker 的旧输出；已有的非空未托管目录会被拒绝。必须走容器：manifest 里的
artifact selector 取的是构建进程的 OS/arch，在 macOS/Windows 上直接构建会得到 Headless 容器
选不中的 artifact。

## 独立发布

VRChat 使用专属的稳定版本 tag：

```text
collector-vrchat/vX.Y.Z
```

[`release-collector-vrchat.yml`](../../../.github/workflows/release-collector-vrchat.yml) 在 `linux/amd64` 容器内
用 tag 版本构建 Package，再生成一个可重复的 zip 和不可变 `release.json`。它们发布到：

```text
https://heartbeat.shenxianovo.com/collector-registry/v1/
  packages/heartbeat.collector.vrchat/versions/X.Y.Z/
```

普通 `main`/PR 不发布；共享 CI 通过 VRChat.Tests 对 release assembler 做 dry-run。服务器上已经存在相同
Version 时，只有字节完全一致才允许把 workflow rerun 当成幂等，否则发布失败。这里没有 current pointer、
自动安装、更新 channel、签名或回滚；它们不属于这条显式发布纵切。

本地已有 Package 时，可只生成待发布文件而不上传：

```bash
./scripts/package-vrchat-release.sh \
  --package .local/collector-packages/vrchat \
  --version 0.1.0 \
  --output /tmp/vrchat-release
```

## 验证与当前交付

```bash
dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests
```

Package 是独立于 Headless 镜像的制品：专属 tag 工作流把它发布到 Registry，Headless 再按 Catalog
下载、校验、安装和运行；换 Package 不需要重建 Hub 镜像。本地构建脚本只用于发版前检查，不参与
日常本地栈启动。运行宿主见
[Headless README](../../hub/Heartbeat.Collection.Headless/README.md)，授权边界见
[ADR-043](../../../docs/adr/043-hub-local-interactive-collector-authorization.md)。
