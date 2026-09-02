# 03 — 共享本地 Collector Package Installation

Status: ready-for-human

Owner: Collection / Collector Host Runtime

Priority: P1 — 这是 ADR-048 的第一条 tracer；没有共享 Installation module，Desktop 与 Headless 的
Package 生命周期就无法脱离各自的 Host 制品。

## What to build

在 `Heartbeat.Collection.Hub` 内提取一个共享的 Collector Package Installation module，让 Desktop
Browser 的 bundled import 和 Headless 的 VRChat 启动走同一段安装逻辑；并把 VRChat Package 从 Headless
image 移出，改为宿主只读挂载的外置 Package 来源。

module 承担的职责：从一个本地 Package 目录安装精确 Collector Package、复用 `LocalCollectorPackage` 的
manifest/artifact/schema/declaration 与 content hash 校验、把已验证内容复制到 Host 数据目录下的稳定
Installation 目录、返回或打开精确的已安装 Package、列出或按精确引用查找 Installation。复制、校验、目录
布局与失败清理细节不对调用方暴露。

Browser 专属的 sideload descriptor、App Instance、reload 与 ExternalHost 状态仍归 Browser adapter；
System Collector 保持 BuiltIn Delivery，不进入这个流程。

## 不在范围内

- Web 下载、静态 Registry index 与 Package source adapter（issue 01/06 之后的 ticket）；
- Collector tag workflow 与 artifact 上传（issue 02）；
- channel/SemVer solver、CheckNow/Approve/Offer、后台更新检查；
- candidate/LKG 自动切换、热更新、自动回滚（issue 04/05，已 wontfix）；
- Ed25519 签名与密钥轮换；
- 安装 journal、断电恢复、cache GC；
- Headless 独立 deploy workflow（issue 07 之后的 ticket）；Backend workflow 只停止顺带构建、推送和重启
  尚未 provision 外置 Package 的 Headless，不在本 issue 内补建新 workflow。

## Acceptance

- [x] 共享 module 位于 `Heartbeat.Collection.Hub`，是一个具体深 module，不为测试公开内部 seam，也不是
      转发 wrapper：复制、校验、目录布局与失败清理由它独占。
- [x] 安装一个有效 Package 后，可按精确引用（packageId + 声明版本 + content hash）从稳定 Installation
      目录重新打开该 Package。
- [x] 重复安装同一个精确 Package 幂等：不重建目录，返回同一个 Installation。
- [x] 非法、损坏或复制后不一致的 Package 不成为 Installation，也不出现在 Installation 列表中。
- [x] 已存在的 Installation 目录内容与精确引用不符时拒绝，不静默覆盖。
- [x] 共享 module 不依赖 Browser、VRChat 或 Desktop 类型。
- [x] Desktop Browser 的 `EnsureBundledPackageInstalled`/`Import` 经由共享 module，用户可见行为不变；
      Browser 专属校验与 sideload 解析仍在 Browser module；Windows 与 macOS 用同一实现。
- [x] 现有 `browser-package-state.json` 与真实 Installation 不被迁移或丢弃。
- [x] Headless 配置的 `packageDirectory` 表示宿主挂载的 Package 来源；初始化时先安装到 Headless 数据
      目录，再从 Installation 启动 ManagedProcess，来源目录不再充当运行时可变目录。
- [x] Headless 的 Runtime/Instance/Activation/Secret/Subject 语义与 Instance key、runtime state 恢复
      行为不变；重启保留 Instance identity。
- [x] Headless Dockerfile 不再 restore/publish VRChat，也不再 COPY Package 目录。
- [x] 存在明确、可重复的本地命令或脚本单独构建 VRChat Collector Package。
- [x] compose 与示例配置以只读 mount 提供宿主 Package 根目录；`.env.example` 与 Headless README 同步。
- [x] ManagedProcess 的精确 Package reference 覆盖运行所需的完整 staged file tree；同 apphost/版本、不同
      code-bearing DLL 得到不同 reference，同时 Browser 的 manifest-hash identity 与既有 state/layout 保持兼容。
- [x] Headless 的公开 `IHostedService`/`Snapshot` 行为证明 Package A 达到 Ready 后，在同一数据目录替换为
      Package B 并重启，会保留 `CollectorInstanceId`、以 B 的精确 reference 再次达到 Ready。
- [x] `CollectorPackageInstallations.List(packageId)` 拒绝路径穿越，且不跟随 Installation root 内指向外部的
      PackageId symlink。
- [x] Bash/PowerShell Package build 先输出到临时 sibling，只替换带 ownership marker 的目的目录；`/`、
      repository root/ancestor、symlink/reparse point 与非空未托管目录都被拒绝。
- [ ] 在安装了 PowerShell 7.2+ 的主机上执行 `build-vrchat-package.ps1` 的危险目的目录负例与真实构建；
      当前主机没有 `pwsh`，只能完成 Bash CLI 行为测试和两份脚本的对称代码审查。
- [x] Backend deploy 不再构建、推送或重启 Headless；Headless 独立 deploy 与服务器 Package provision gap
      在 roadmap 中保持显式未完成。

## Verification

- `dotnet test collection/hub/Heartbeat.Collection.Hub.Tests/Heartbeat.Collection.Hub.Tests.csproj -c Release`
- `dotnet test collection/hub/Heartbeat.Collection.Headless.Tests/Heartbeat.Collection.Headless.Tests.csproj -c Release`
- `dotnet test collection/desktop/Heartbeat.Desktop.Windows.Tests/Heartbeat.Desktop.Windows.Tests.csproj -c Release`
- `dotnet test collection/desktop/Heartbeat.Desktop.Mac.Tests/Heartbeat.Desktop.Mac.Tests.csproj -c Release`
- `dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests/Heartbeat.Collector.VRChat.Tests.csproj -c Release`
- `node scripts/collector-contracts.mjs check`
- 独立执行一次 VRChat Package 构建，并证明 Headless image 在不构建 VRChat 项目的情况下 build 成功。

真实服务器上的「只替换 VRChat Package + 重启 Headless」smoke 属于人工门禁，不在本 issue 内宣称通过。

### 2026-09-01 实测结果（本机 macOS arm64，Docker Desktop）

- Hub tests **265 passed / 0 failed**（本 issue 新增 9 条 Installation 用例 + 1 条 Browser bundled import 用例）。
- Headless tests **9 passed / 0 failed**（新增 3 条：外部来源安装并建立 Instance、来源目录只读仍可安装、
  同一数据目录重启保持 `CollectorInstanceId`）。
- Desktop Windows tests **47 passed**、Desktop Mac tests **78 passed**、VRChat collector tests **15 passed**。
- `node scripts/collector-contracts.mjs check` → `Collector Fact Schemas and evolution baseline are consistent.`
- `./scripts/build-vrchat-package.sh` 单独构建 Package 成功：产出 `collector-manifest.json` 等 17 项，
  selector `os=linux` / `arch=arm64`（Package 在 linux 容器内产出，与 Headless 运行环境一致）。
- `docker build -f collection/hub/Heartbeat.Collection.Headless/Dockerfile .` 成功，构建日志只 restore/publish
  Headless + Hub + Core，镜像内 `/app/vrchat-package` 不存在。
- `docker compose -f compose.yml config` 与 `docker compose -f compose.local.yml --env-file .env.local.example config`
  通过，`/package-source` 为只读挂载且 `create_host_path: false`。
- `scripts/start-local.sh` 的 Package 预检负例实测：来源目录缺 `collector-manifest.json` 时 exit 1 并提示先构建。

未验证（明确不宣称）：

- `scripts/build-vrchat-package.ps1` 与 `scripts/start-local.ps1`：本机无 `pwsh`，只做人工比对。
- 没有执行 `docker compose up`，所以「Headless 真从 `/package-source` 安装并拉起 VRChat」的端到端未跑。
- 真实服务器上的 Package 替换 + 重启 smoke 未做，仍是 issue 07 的人工门禁。

### 2026-09-02 修复验证（本机 macOS arm64，Docker Desktop）

- 严格纵切 red/green：
  - VRChat builder regression 首次失败：同 apphost/版本、不同 DLL 的 `PackageContentHash` 相同；修复后通过。
  - Installation public `List` regression 首次 **2 failed**：`..` 读取 root 外 Installation、PackageId symlink
    跟随到 root 外；修复后 **2 passed**。
  - Headless A→B public behavior 首次因 Snapshot 不暴露精确 Package reference 编译失败；补齐公开状态后通过，
    且所有 Headless installation tests 改为只在 `Phase == Ready` 时成功，`Failed` 会立即使测试失败。
  - Bash CLI regression 首次失败并显示非空未托管目录的 sentinel 已被删除；修复后通过。
- `dotnet test collection/hub/Heartbeat.Collection.Hub.Tests/Heartbeat.Collection.Hub.Tests.csproj -c Release`
  → **267 passed / 0 failed**。
- `dotnet test collection/hub/Heartbeat.Collection.Headless.Tests/Heartbeat.Collection.Headless.Tests.csproj -c Release`
  → **10 passed / 0 failed**。
- `dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests/Heartbeat.Collector.VRChat.Tests.csproj -c Release`
  → **18 passed / 0 failed**。
- `node scripts/collector-contracts.mjs check` →
  `Collector Fact Schemas and evolution baseline are consistent.`
- `./scripts/build-vrchat-package.sh --output <temporary sibling>/vrchat` 真实 Docker 构建成功；对同一带 ownership
  marker 的输出再次执行也成功。
- `docker build -f collection/hub/Heartbeat.Collection.Headless/Dockerfile ...` 成功；运行镜像检查确认既没有
  `/app/vrchat-package`，也没有 `/package-source`。
- `docker compose -f compose.yml config` 与
  `docker compose -f compose.local.yml --env-file .env.local.example config` 均通过（前者仅报告未注入 secrets 的
  预期 warning）。
- `./scripts/start-local.sh` 完整起栈通过；过程中发现 Backend Docker build 未复制
  `collection/contracts/segment-rotation-policy.json`，补齐该明确 build input 后 Backend、Frontend、Headless 与
  Postgres 均正常运行。Headless 从 `/package-source/vrchat` 安装到
  `/data/collector-packages/heartbeat.collector.vrchat/0.1.0/f2b09b...`，VRChat 子进程从该 Installation 启动；
  owner 完成真实 VRChat 登录后，Dashboard 登录管理显示 `Ready/已登录`，Runtime 持久化的
  `PackageContentHash` 与 Installation 目录 hash 一致。匿名管理请求返回预期 `401`。
- `git diff --check` 通过；新增 C# 文件均为 `0644`。

本轮未验证（明确不宣称）：

- 主机没有 `pwsh`，未执行 `build-vrchat-package.ps1` / `start-local.ps1`。
- 未做真实服务器 Package provision 或 Package 替换 + Headless 重启 smoke。
- 未重跑 Desktop Windows/Mac tests；Browser compatibility 由 Hub 内现有 Browser package/state tests 覆盖。

## Dependencies

依赖 [ADR-048](../../../docs/adr/048-shared-collector-host-runtime-and-independent-release-units.md) 与
[roadmap](../../../docs/architecture/collector-delivery-implementation-roadmap.md) 的第一条 tracer 定义。
issue 01/02/06/07 依赖本 issue 建立的 Installation seam。

## Comments

- 2026-09-01：原规格是 Registry 时代的「版本目录 + 安装事务 + 完成标记」，已随 ADR-045/047 实现撤回。
  按 ADR-048 重写为共享本地 Installation module 与外置 VRChat Package 两件事，不恢复 candidate/approval/
  LKG 切换状态机。

- 2026-09-01：实现落在 `collection/hub/Heartbeat.Collection.Hub/Collectors/Packaging/CollectorPackageInstallations.cs`。
  Installation 事实的唯一权威是文件系统里的稳定目录（只有校验通过的 staging 才 rename 到最终路径），
  所以没有引入第二份安装状态账本。目录布局与 tree hash 算法与 Browser 原实现逐字节一致，
  已有 `browser-package-state.json` 与已装好的 Installation 不需要迁移。
- 2026-09-01：重构中发现并修掉一个降级语义回归。`LoadCurrentPackageLocked` 改走共享 module 后，
  安装副本内容漂移抛的是 `CollectorRuntimeStateException` 而不再是 `PackageValidationException`，
  而 `BuildSnapshotLocked` 只 catch 后者，会把 host 打崩。现在两类异常都只降级为 Degraded，
  由既有的 `Current_WhenInstalledPayloadChanges_...` 与 `Startup_WhenInstalledPayloadChanged_...` 兜住。
- 2026-09-01：Headless Package 来源目录的**分发**仍没有承接人——服务器上没人负责把 Package 放到
  `HEADLESS_PACKAGE_SOURCE_PATH`，`create_host_path: false` 会让首次部署直接挂掉。这属于 ADR-048 的
  Collector tag workflow（issue 02）与部署拆分（issue 07），已在 roadmap「当前差距」登记，本 issue 不代做。
