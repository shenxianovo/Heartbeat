# 04 — 批准精确已安装候选

Status: ready-for-human

Owner: Collection / Management

Priority: P1 — 自动发现/下载不能被误写成 owner 已批准或运行已更新。

## What to build

实现窄的 update management interface：`Current`、`CheckNowAsync`、
`ApproveAsync(exactPackageRef)`。它展示 PackageId、Version 与 content hash，使用现有 AuthService 身份做
owner authorization，但不引入 opaque token、审批审计或重放工作流。

## Acceptance

- [ ] candidate 绑定 InstanceId、PackageId、from/to exact refs、content hash 与 compatibility result；批准时
  必须仍与 Current 展示的精确候选一致。
      （已绑定 InstanceId、PackageId、当前运行 Version 与 content hash、已安装/已批准 exact ref；
      compatibility result 需要 handshake，属 issue 05。）
- [x] `CheckNowAsync` 可刷新/下载/验证但不改变 Activation、Desired State 或 LKG。
- [x] `ApproveAsync` 要求当前登录 owner 有该本地 Instance 的权限，拒绝跨 owner、跨 Instance、未完成安装
  或内容不匹配的 exact ref，并返回稳定结果。
- [x] 已下载验证的 exact ref 即使不再是 Registry current 仍可批准；approval 不重新解析 latest。
- [ ] Current 至少区分 no update、checking、installed/awaiting approval、approved/starting、ready 与 failed。
      （no update / awaiting approval / approved / failed 已可由投影字段唯一判定；starting 与 ready 属
      issue 05，checking 在同步 CheckNow 下不是可观察状态。）
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
