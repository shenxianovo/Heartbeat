# 04 — 批准精确已安装候选

Status: needs-triage

Owner: Collection / Management

Priority: P1 — 自动发现/下载不能被误写成 owner 已批准或运行已更新。

## What to build

实现窄的 update management interface：`Current`、`CheckNowAsync`、
`ApproveAsync(exactPackageRef)`。它展示 PackageId、Version 与 content hash，使用现有 AuthService 身份做
owner authorization，但不引入 opaque token、审批审计或重放工作流。

## Acceptance

- [ ] candidate 绑定 InstanceId、PackageId、from/to exact refs、content hash 与 compatibility result；批准时
  必须仍与 Current 展示的精确候选一致。
- [ ] `CheckNowAsync` 可刷新/下载/验证但不改变 Activation、Desired State 或 LKG。
- [ ] `ApproveAsync` 要求当前登录 owner 有该本地 Instance 的权限，拒绝跨 owner、跨 Instance、未完成安装
  或内容不匹配的 exact ref，并返回稳定结果。
- [ ] 已下载验证的 exact ref 即使不再是 Registry current 仍可批准；approval 不重新解析 latest。
- [ ] Current 至少区分 no update、checking、installed/awaiting approval、approved/starting、ready 与 failed。
- [ ] MVP 不提供批量批准、approval audit、opaque token、withdrawn 或 Browser reload 状态。
- [ ] 现有 authenticated Hub/Headless management API 暴露 Current、手动 CheckNow 与 Approve；MVP 不新增
  Dashboard 页面、后台检查、timer 或通知，System 不出现 Web update action。
- [ ] 授权、非 current 但已安装候选、并发 approve 与 restart 后读取当前批准 ref 的测试通过。

## Dependencies

依赖 issue 03；遵循 ADR-043 的 local interactive authorization 边界。
