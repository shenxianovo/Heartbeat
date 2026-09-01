# Collector Package Registry：独立构建与 Web 交付

Status: ready-for-human

剩余的全部是人工门禁，没有 agent 可承接的实现范围（见 issue 07）：真实 `collector-vrchat/vX.Y.Z` tag 的发布、
真实域名/独立静态目录部署与反向代理映射、服务器上的 VRChat 端到端 smoke。

## Problem

非内置 Collector 已有 Package / Instance / Activation 与 Execution Driver 语义，但制品仍主要随
Desktop / Headless 构建交付。仓库缺少一个能让每个 Collector 独立显式发布、让 Runtime 验证和
下载、并让 owner 对具体 Instance 明确批准的 Web 交付闭环。

旧 source-level Collector Registry 是配置/声明遗留账本，不能复用为包仓库；现有 AuthService
只用于证明本地 owner 的批准权限。开发期 MVP 不建立独立制品签名系统。

## Outcome

- 第一条纵切让 VRChat 用自己的 tag 独立构建和发布。
- Runtime 从同域名静态 Registry 发现、下载并按长度与 SHA-256 验证精确候选。
- owner 通过现有认证管理面批准界面展示的 PackageId、Version 与 content hash；批准不等于 Ready。
- VRChat ManagedProcess 候选 Ready 后才接管，旧 Last-Known-Good 在此之前保持可用。
- Browser、生产签名、多平台矩阵、迁移与运维加固在纵切完成后重新裁决。

生产目标见 [ADR-045](../../docs/adr/045-independent-web-delivery-for-collector-packages.md)，开发期缩减见
[ADR-047](../../docs/adr/047-lean-development-collector-web-delivery.md)。
跨 feature、feature 内部与各 Execution Driver 的依赖图见
[Collector 独立交付实现路线图](../../docs/architecture/collector-delivery-implementation-roadmap.md)。

## Fixed decisions

### Development release and hosting

- MVP 只允许官方 Package；不做第三方上传、动态 DB/admin API、多 channel 或自动批准。
- System 使用 BuiltIn Delivery，随 Desktop 发布，不产生 Web offer。
- 第一条纵切只发布当前 Headless 实际环境可运行的 framework-dependent VRChat zip，由
  `collector-vrchat/vX.Y.Z` 显式 tag 触发。
- Registry 为每个 Package 暴露 `/packages/{packageId}/current.json`，绑定 Version、URL、length 与
  SHA-256；artifact 位于 `/packages/{packageId}/versions/{version}/`。已发布 Version 不可覆盖，修复发
  新 tag；不实现 channel、SemVer range solver 或完整平台矩阵。
- Registry 是独立静态部署单元，经反向代理服务于
  `https://heartbeat.shenxianovo.com/collector-registry/v1/`；不进入 frontend image 或 `/u/` 路由。
- 普通 `main` 构建只验证，不发布用户可见候选。
- Registry 使用当前服务器独立静态目录，由反向代理暴露；artifact 先上传，最后替换 `current.json`。

### Integrity and authorization

- Registry 公开读取并依赖现有 HTTPS；MVP 不实现 Ed25519、key id/rotation、withdrawn 或独立 trust root。
- Registry 记录绑定 artifact length + SHA-256；Package loader 继续验证内部 manifest/artifacts/schema/
  declarations，解压不得逃逸目标目录。
- AuthService 只授权“这个 owner 是否能批准这个本地 Instance 的精确候选”，不新增审批系统。
- `current.json` 不声明 host/protocol compatibility matrix；现有 Package loader 与 Collector Protocol 握手
  是兼容性的唯一执行门禁。

### State and interface

- MVP Desired State 只保留 enable/config intent 与当前批准的精确 Package reference；channel 与 SemVer
  range 推迟。
- Resolved Set / Installation = exact version + content hash；Installation 使用 content-addressed 路径。
- 外部管理 interface 共四个 owner 动作：`Current`、`CheckNowAsync`、`ApproveAsync(exactPackageRef)` 与显式
  `SwitchToApprovedAsync`。批准不隐含接管，只有 Switch 才启动已批准候选；两者合一会让批准隐含接管，违反
  「Ready 前保留旧 LKG」与「失败后等待人工再次触发」（ADR-047）。
- 内部 delivery seam 负责读取 index、download、length/hash/package validation、独立版本目录和完成标记，
  不负责 Activation；不实现原子安装 journal、离线目录、cache GC 或全局 solver。
- 失败不改写 Desired State；未完成安装不发布为 Installation；候选失败不破坏 per-Instance LKG。
- MVP 不提供“全部批准”、approval audit、opaque token 或 replay workflow。
- 只有手动 `CheckNowAsync`；不做后台 timer、轮询或通知。第一版只暴露现有 authenticated
  Hub/Headless management API，不建设 Dashboard 页面。
- 切换同样只有手动一次：`POST /hub/api/v1/collector-instances/{id}/package-update/switch`。它不排期、不重试，
  也不能用「重启宿主」代替——宿主重启只启动已经到达过 Ready 的那份 Package。
- exact Package ref 下载并验证后，即使 Registry current 已指向新版本仍可批准；新版本留到下次手动检查。
- 下载、校验或启动失败保存最后结构化错误，当前/LKG 不变；不自动重试或静默清除候选。

### Driver-specific success

- VRChat ManagedProcess：精确候选 Activation Ready 后接管并视为更新成功；Ready 前保留旧 LKG，Ready
  后退出按普通运行故障处理，不新增候选稳定事务。批准与切换是两次 owner 动作；宿主重启只会启动已经到达过
  Ready 的那份 Package，不会让未 Ready 的已批准候选靠重启接管（issue 05）。
- ExternalHost Browser 与 Host upgrade compatibility preflight 不属于第一条纵切。
- 不迁移旧 bundled VRChat Package；它继续作为旧 LKG，第一个 Web release 走普通候选流程。

## Entry condition

开始改变真实 VRChat Activation 前，先修复 Activation lifetime issue 09 的 termination cause 与 durable
evidence，并把相关墙钟回归改成确定性测试。Gap dead-letter 双文件崩溃原子性作为开发期已知限制，
不阻塞 MVP。

## Out of scope

- 第三方 Package、签名 trust roots、Registry 写 API。
- 自动批准、强制更新、远程 kill switch。
- Desktop / Headless host 自更新与全局 host-upgrade preflight。
- Analytics + Dashboard 的协调发布原子性；它属于应用发布平面。
- Browser Web 更新、全平台矩阵、离线 Registry、withdrawn、cache GC、完整迁移与生产运维演练。
- 为没有现场证据的旧版本、旧 outbox 或旧安装格式承诺兼容。

## Delivery graph

1. [01 — 定义最小静态 Registry index](issues/01-static-registry-index.md)
2. [02 — 建立 VRChat 显式 tag release pipeline](issues/02-explicit-collector-release-pipeline.md)
3. [03 — 实现版本目录安装与完成标记](issues/03-version-directory-installation.md)（依赖 01）
4. [04 — 暴露精确候选与 owner approval](issues/04-exact-package-approval.md)（依赖 03）
5. [05 — 接入 VRChat ManagedProcess Ready 切换](issues/05-vrchat-ready-switch.md)（依赖 04；已 done）
6. [07 — 部署开发 Registry 并完成 VRChat smoke](issues/07-deploy-and-vrchat-smoke.md)（依赖 01–05 与选定 P2 gate）
7. [06 — Browser ExternalHost Web 更新](issues/06-browser-external-host-update.md)（MVP 后重新裁决）

01、02 可并行。07 保留域名路由与真实 VRChat smoke 的人工门禁；不再等待 Browser 或 production
signing key。

## MVP exit conditions

- [x] Activation lifetime issue 09、10 完成：termination cause、durable evidence 与相关墙钟回归可信。
      （2026-09-01：两张 issue 均为 `done`，见 `.scratch/collector-activation-lifetime/issues/`；本条是 entry
      condition，不依赖真实服务器。）
- [ ] `collector-vrchat/vX.Y.Z` dry-run 与真实 framework-dependent artifact 发布通过。
      （2026-08-31：本地 dry-run 已通过并自校验，见 issue 02；真实 tag 与上传是 issue 07 的人工门禁。）
- [ ] `/collector-registry/v1/packages/heartbeat.collector.vrchat/current.json` 经真实域名可读，length/hash
      与 artifact 一致（路径按真实 PackageId 组织，不是 `packages/vrchat/`）。
- [ ] Headless 手动 CheckNow、版本目录 Installation、authenticated exact-ref approval 与真实 Ready 通过。
      （2026-08-31：版本目录 Installation 与完成标记见 issue 03；Headless authenticated 手动 CheckNow 与
      exact-ref approval 已接线并测试，见 issue 04；Ready 切换已接线并由真实 VRChat 子进程测试覆盖，见
      issue 05——批准之后需要 owner 再调一次 `/package-update/switch`；真实域名 smoke 是 issue 07。）
- [ ] 错 hash、损坏 Package、incompatible handshake 与 never-ready candidate 都保留旧 LKG 并显示最后错误。
      （2026-08-31：错 hash 与损坏 Package 的结构化最后错误已持久化在 Collector Runtime State 并由管理面
      Current 展示，旧 LKG 与既有 Installation 不受影响，见 issue 04；incompatible handshake、never-ready
      与启动失败都在候选 Ready 之前失败，旧 Package 重新激活、旧 LKG 不被覆盖，reason 投影为
      `Incompatible` / `ReadyTimeout` / `StartupFailed`，见 issue 05。真实服务器上的复现仍是 issue 07。）
- [ ] 真实服务器完成一次端到端 smoke，tracker 记录证据；Browser、签名、自动检查与 Dashboard UI 不作为
  完成条件。

## Comments

### 2026-09-01 — 双轴复审收口

- **P1「/switch 是第 4 个 owner 动作」按追认文档处理，不回退代码**：ADR-047 正文与本 PRD 的 Fixed decisions /
  外部管理 interface 都补上了第四个动作。理由是 ADR-047 自己写的「批准不等于 Ready」：把切换折进 Approve，
  批准就隐含了接管，反而违反「Ready 前保留旧 LKG」与「失败后等待人工再次触发、不自动重试」。所以 Switch 必须
  是独立、显式的 owner 动作（P3 第 2 条一并解决）。
- **P2「PRD Status 漂移」已清**：`ready-for-agent` → `ready-for-human`，并在顶部写明剩余的三条人工门禁：
  真实 tag 发布、真实域名/独立静态目录部署与反向代理映射、服务器上的 VRChat 端到端 smoke（全部由 issue 07
  承接）。
- **P2「ADR-047 未同步 restart 收敛规则」已清**：ADR-047 正文补上「宿主重启按 effective Package 收敛」及其
  理由；此前这条只记在 issue 05 与 `collection/CONTEXT.md`。
- **P3「exit condition 路径写成 `packages/vrchat/`」已清**：改成真实 PackageId
  `packages/heartbeat.collector.vrchat/current.json`；全库 `packages/vrchat` 扫描只剩 issue 07 里那句刻意的
  反面提醒。
- **复审疑点收口**：`collection/CONTEXT.md` 的 Collector Update Offer 条目删掉了「绑定宿主兼容结果」——兼容性
  不在 Offer 里预判，由 Package loader 与 Collector Protocol 握手在 Ready 路径上裁决；`registryBaseUri` 未配置
  时返回 `RegistryNotConfigured` 保持不变。
- **issue 状态一览**：01 `done`、02 `ready-for-human`（真实 tag/上传/CI 接线）、03 `done`、04 `done`（本次由
  `ready-for-human` 收口）、05 `done`、06 `needs-triage`（MVP 后重新裁决）、07 `ready-for-human`（部署与真实
  smoke）。
- 验证：`git diff --check` 无输出；`dotnet build Heartbeat.slnx --no-restore -c Debug` → 0 Warning / 0 Error；
  `dotnet test Heartbeat.slnx --no-build` → 1223 passed / 0 failed（基线 1219 + 新增 4：跨 owner 门禁 3 条 +
  宿主崩溃 1 条）。
