# 02 — VRChat Collector 显式 tag 与不可变 Web Release

Status: done

Owner: Build / Release

Priority: P1 — 用第一份真实 Web Release 证明非 BuiltIn Collector 可以脱离 Desktop、Headless、Frontend 与
Analytics 独立发版，并给后续 Web Package source adapter 一个真实制品。

## What to build

只建立 VRChat Collector 的显式发布纵切：

1. 只有 `collector-vrchat/vX.Y.Z` tag 能触发发布；tag 版本进入程序集、Package manifest 与 Release metadata。
2. 在 `linux/amd64` 容器内构建当前生产 Headless 目标的 framework-dependent Collector Package。
3. 把 Package 组装为可重复的 zip，并生成同目录的不可变 `release.json`，记录精确 URL、字节长度与 SHA-256。
4. 先上传到服务器隔离 staging，再原子加入静态 Registry 的精确 Version 目录；已存在版本只允许完全相同的
   workflow rerun，任何字节差异都拒绝覆盖。
5. 发布后从 `https://heartbeat.shenxianovo.com/collector-registry/v1/` 公网回读 artifact 与 metadata，逐字节
   比较本次构建结果。

静态目录由服务器 Caddy 直接提供，不进入 Frontend image；发布不构建、不重启 Desktop、Headless、Frontend
或 Analytics。

## Release contract

```text
/collector-registry/v1/
  packages/heartbeat.collector.vrchat/versions/{version}/
    heartbeat.collector.vrchat-{version}-linux-x64.zip
    release.json
```

`release.json` v1 只描述一个精确 Release：`schemaVersion`、`packageId`、`version`、`target { os, arch }` 与
`artifact { fileName, url, length, sha256 }`。它不是 channel/current pointer，不做版本解析。

## Acceptance

- [x] `.github/workflows/release-collector-vrchat.yml` 只监听 `collector-vrchat/v*`，并用严格正则接受稳定
      `X.Y.Z`；普通 `main`、PR 与 Desktop tag 不发布 VRChat。
- [x] tag 版本进入 VRChat 程序集、API user agent 与 `collector-manifest.json`，Package manifest/tag 不一致时
      release assembler fail closed。
- [x] tag job 固定 `linux/amd64`，产物只声明一个 `managedProcess + linux + x64` artifact，不生成
      self-contained 或多平台矩阵。
- [x] `package-vrchat-release.sh` 从已构建 Package 生成确定性 zip 与 `release.json`；metadata 绑定同域 HTTPS
      精确 URL、真实 length 与 SHA-256。
- [x] zip 解包后 manifest 位于根目录，ManagedProcess entrypoint 保留 executable bit。
- [x] 服务器发布只追加 `/versions/{version}`：先进入同父目录 staging，再 rename；同版本同字节幂等，
      同版本异字节拒绝覆盖。
- [x] workflow 在发布后经公网回读 zip 与 `release.json`，与 runner 上的构建结果逐字节比较。
- [x] 普通 Collector Contracts CI 会运行 VRChat.Tests，其中 release assembler 的本地 dry-run 覆盖确定性、
      metadata 与 Package/tag 版本冲突；VRChat release 本身不构建或校验 Browser，跨 Collector 契约继续由
      独立的 `collector-contracts.yml` 负责。
- [x] System、Browser、current pointer、Runtime 下载/安装、签名、channel、撤回、回滚与多平台矩阵不进入本
      issue。
- [x] owner 已在生产服务器配置 Caddy 静态路由；不存在的 Registry 路径经公网返回空 404，没有回落到
      Dashboard。
- [x] 修复后的 `collector-vrchat/v0.2.0` tag 已重新推送；真实 workflow、服务器 publish 与公网逐字节
      回读全绿。Headless 执行主机的 CPU 架构属于后续安装/运行 smoke，不是静态发布事实。

## Verification

- `dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests/Heartbeat.Collector.VRChat.Tests.csproj -c Release`
- `node scripts/collector-contracts.mjs check`
- `bash -n scripts/package-vrchat-release.sh`
- 用本地 Package 连续生成两份 Release，zip 字节完全相同，metadata 的 length/hash 与文件一致。
- YAML parse 与 `git diff --check`。

2026-09-02 本机验证：VRChat.Tests **21 passed / 0 failed**；Collector contracts 通过；真实
`linux/amd64` Docker Buildx 以 `COLLECTOR_VERSION=0.2.0` 连续构建两次，两份 Package 都声明
`version=0.2.0`、`managedProcess + linux + x64`，组装后的 zip 与 `release.json` 逐字节相同；zip 解包后
entrypoint 保留 executable bit。全仓 Release build 为 0 warning / 0 error，12 个测试项目合计
**1038 passed / 0 failed**；`actionlint`、ShellCheck、YAML parse、`bash -n`、IDE1006 格式门禁与
`git diff --check` 通过。

首次 `collector-vrchat/v0.2.0` 运行在 VRChat.Tests 之后被错误加入的 repository-wide
`collector-contracts.mjs check` 拦住：该命令需要先构建 Browser `dist`，与本 Release 单元无关，且尚未进入
Docker build 或 publish。现已从专属 release workflow 删除 Node/Browser 契约门禁；服务器 staging、不可变
冲突路径与 Caddy 公网回读当时仍未执行。

修复后的 run `33629577645` 在 commit `b00a6cd` 上完成 build 与 publish。公网 `release.json` 声明
`heartbeat.collector.vrchat` 0.2.0、`linux-x64`、artifact 长度 `1311885` 与 SHA-256
`41108dcaf079c82ccd000a961ec971fe98d1e0c70e4b0922e758bd1f23442471`；独立下载的 zip 与两项声明一致。

## Non-goals

- 不实现 `current.json`、stable channel、SemVer solver 或 Runtime Web Package source adapter；
- 不自动安装、批准、激活或替换 Headless 中正在运行的 Collector Instance；
- 不建立 Ed25519、第三方市场、撤回、LKG、自动回滚或 cache GC；
- 不发布 Browser，也不改变 System BuiltIn Delivery；
- 不创建或部署 Headless workflow。

## Dependencies

- [ADR-048](../../../docs/adr/048-shared-collector-host-runtime-and-independent-release-units.md) 的独立发布单元边界；
- [issue 03](./03-shared-local-package-installation.md) 已提供外置 VRChat Package builder 与共享 Installation。

后续 Web Package source adapter 应消费本 issue 的真实 immutable Release，再决定是否需要独立的 current
pointer；不得提前恢复 ADR-045/047 已撤回的 approval/LKG 状态机。

## Comments

### 2026-09-02 — 从旧 Registry 规格缩成单一发布纵切

旧 issue 同时要求 mutable index、发布工具、Runtime reader 与完整本地 Registry fixture，实际上把 artifact
发布和发现/下载混成了一个 feature。本轮只落不可变精确 Release：它已经能独立下载，也为下一条 Web source
adapter 提供真实输入；没有 current pointer 就没有第二份“当前版本”权威。

### 2026-09-02 — 首次 tag 暴露跨发布单元门禁

run `33629213290` 的 VRChat.Tests 21/21 通过，但 release job 随后调用 repository-wide
`collector-contracts.mjs check`；它因 Browser `dist` 未构建而失败。修复不是在 VRChat release 里补 Browser
build，而是删除这条跨 Collector 依赖。Fact Schema evolution 与 Browser payload 一致性仍由
`collector-contracts.yml` 在 PR/main 上独立验证。

### 2026-09-02 — v0.2.0 真实发布完成

run `33629577645` 全绿，Caddy 精确 Version 路径可公网读取；runner 的发布后逐字节回读和本地独立下载复核
均通过。issue 02 的静态发布纵切完成。Host 从 Web 下载、安装并运行该 Package 仍是后续 feature。
