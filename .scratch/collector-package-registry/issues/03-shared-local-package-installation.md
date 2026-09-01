# 03 — 共享本地 Collector Package Installation

Status: done

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
- Headless/Backend deploy workflow 拆分（issue 07 之后的 ticket）。

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
