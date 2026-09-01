# 04 — 批准精确已安装候选

Status: done

Owner: Collection / Management

Priority: P1 — 自动发现/下载不能被误写成 owner 已批准或运行已更新。

## What to build

实现窄的 update management interface：`Current`、`CheckNowAsync`、
`ApproveAsync(exactPackageRef)`。它展示 PackageId、Version 与 content hash，使用现有 AuthService 身份做
owner authorization，但不引入 opaque token、审批审计或重放工作流。

## Acceptance

- [x] candidate 绑定 InstanceId、PackageId、from/to exact refs 与 content hash；批准时必须仍与 Current 展示的
  精确候选一致。
      （2026-09-01 措辞收口：原文还要求绑定 compatibility result，但 ADR-047 明写「现有 Package loader 与
      Collector Protocol 握手是兼容性的唯一执行门禁」，兼容性不在 Offer/candidate 里预判，因此投影里没有、
      也不应有兼容结果字段；握手裁决发生在 issue 05 的 Ready 路径上。）
- [x] `CheckNowAsync` 可刷新/下载/验证但不改变 Activation、Desired State 或 LKG。
- [x] `ApproveAsync` 要求当前登录 owner 有该本地 Instance 的权限，拒绝跨 owner、跨 Instance、未完成安装
  或内容不匹配的 exact ref，并返回稳定结果。
- [x] 已下载验证的 exact ref 即使不再是 Registry current 仍可批准；approval 不重新解析 latest。
- [x] Current 至少区分 no update、installed/awaiting approval、approved（尚未接管）、ready（已接管）与
  failed。
      （由投影字段唯一判定：无候选 / `InstalledCandidate` / `ApprovedCandidate` 与 `CurrentVersion` 不等 /
      已批准候选成为 `CurrentVersion` + `LastKnownGood` / `LastFailure`。2026-09-01 措辞收口：`checking` 与
      `starting` 在同步的一次性 CheckNow 与一次性 switch 下不是可观察状态——一次调用返回的就是结果；ready
      的证据在 issue 05。）
- [x] MVP 不提供批量批准、approval audit、opaque token、withdrawn 或 Browser reload 状态。
- [x] 现有 authenticated Hub/Headless management API 暴露 Current、手动 CheckNow 与 Approve；MVP 不新增
  Dashboard 页面、后台检查、timer 或通知，System 不出现 Web update action。
- [x] 授权、非 current 但已安装候选、并发 approve 与 restart 后读取当前批准 ref 的测试通过。

## Dependencies

依赖 issue 03；遵循 ADR-043 的 local interactive authorization 边界。

## Comments

### 2026-08-31 — 实现与证据

窄更新管理面已落在 `CollectorPackageUpdateService`：`Current` / `CheckNowAsync` /
`Approve(exactRef)`，由 Headless 的既有 authenticated route group 暴露：

- `GET /hub/api/v1/collector-instances/{id}/package-update`
- `POST /hub/api/v1/collector-instances/{id}/package-update/check`
- `POST /hub/api/v1/collector-instances/{id}/package-update/approval`（body：`packageId`、`version`、
  `artifactSha256`）

三个 endpoint 与既有 `/subjects`、交互授权 endpoint 同在 `MapGroup("/hub/api/v1").RequireAuthorization()`
内，沿用 host 既有 OIDC bearer（owner `sub` + `client_id`）；未认证一律 401，非本 Hub 的 Instance 一律 404。

状态唯一：更新事实写在 Collector Runtime State（`collector-runtime.json`，`JsonCollectorRuntimeStore`
schema v3 的 per-Instance `packageUpdate` 记录）里，与 Last-Known-Good 同处一份账本；`Current` 只是投影。
新增结构化 reason：`RegistryNotConfigured`、`CollectorInstancePackageMismatch`。

行为边界：一次 CheckNow = 一次 index 读 + 一次下载校验 + 一次安装，失败写结构化 last error 并保留既有
Installation、已批准候选与 LKG，不重试、不排期；Approve 只查 `CollectorInstallationStore.OpenInstallation`，
不访问 Registry，因此已安装但不再 current 的 exact ref 仍可批准；批准不切换 Ready。

测试：Hub core 26 个（真实 loopback 静态 Registry + 真实 VRChat Package，含不可达/畸形 index/长度与 hash
不匹配/损坏归档、不自动重试、restart 后仍读到同一批准 ref、并发 approve 收敛、CheckNow 不改 Desired/LKG）；
Headless 3 个（route group 授权元数据、未认证 401、未知 Instance 404）。全量 `dotnet test Heartbeat.slnx`
通过。

剩余门禁：Ready 切换与 compatibility result 属 issue 05；真实域名 Registry 的端到端 smoke 属 issue 07。
API 层的 approve 200 快乐路径未在 Headless 测试中重复（需要真实 Installation fixture），由 Hub core 测试覆盖。

### 2026-09-01 — 双轴复审收口

- **P2「跨 owner 门禁零覆盖」已清**：新增 `HeadlessOwnerGateTests`（`Heartbeat.Collection.Headless.Tests`），
  跑的是 host 自己的 bearer 配置。为此把 `Program.cs` 里内联的 OIDC 配置原样提取成
  `HeadlessOwnerAuthentication.AddHeadlessOwnerAuthentication(management)`——行为零变化，host 与测试从此走
  同一段代码。三条用例：真实签名、真实有效期、`at+jwt` 类型正确但 `sub` 是别人 → 四个 package-update
  endpoint 全部 401；`sub` 对但 `client_id` 不对 → 全部 401；owner 自己的 token → 全部 404（证明 401 不是
  「什么都拒」）。只有签名密钥是测试自己的：Hub 的密钥来自 OIDC discovery，那是部署事实，不是「这个 owner
  能不能管这个 Hub」。反向验证：把 `OnTokenValidated` 的判定短路掉，前两条立刻失败。
  Acceptance#3 因此是真的勾上，不再是「勾了但没测」。
- **Acceptance 与实现逐条对齐**：原先未勾的两条已按实际情况收口（见上）。compatibility result 不是漂移的
  待办，而是 ADR-047 明确不做的事——兼容性由 Package loader 与握手在 Ready 路径上裁决，`collection/CONTEXT.md`
  的 Collector Update Offer 条目同日删掉了「绑定宿主兼容结果」的措辞。`ready` 的证据是 issue 05 的
  `CollectorPackageSwitchTests` 与 `VRChatManagedProcessCollectorTests`。
- **Status 从 `ready-for-human` 改为 `done`**：本 issue 的 Acceptance 全部完成且有自动化证据，自身没有任何
  人工门禁。真实域名 Registry 的端到端 smoke 不是本 issue 的 Acceptance，它是 issue 07 的
  「真实 ManagedProcess smoke：手动检查 → 下载/验证 → authenticated API 批准 → authenticated API 切换 →
  Ready」那一条，由 issue 07 承接（issue 03、05 与本 issue 的关系一致）。
- 验证：`dotnet build Heartbeat.slnx --no-restore -c Debug` → 0 Warning / 0 Error；
  `dotnet test Heartbeat.slnx --no-build` → 1223 passed / 0 failed（基线 1219 + 新增 4）。
