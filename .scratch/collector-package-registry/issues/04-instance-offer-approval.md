# 04 — 暴露 per-Instance offer 与 owner approval

Status: ready-for-agent

Owner: Collection / Management

Priority: P1 — 自动发现/下载不能被误写成 owner 已批准或运行已更新。

## What to build

实现窄的 update management interface：`Current`、`CheckNowAsync`、
`ApproveAsync(opaqueOfferId)`。它把已验证 candidate 投影为 per-Instance offer，使用现有 AuthService
身份做 owner authorization，但不向 UI 暴露 URL、文件路径、solver 或 Registry metadata。

## Acceptance

- [ ] offer 绑定 InstanceId、PackageId、from/to exact refs、content hash、compatibility result、withdrawn
  状态与不可预测的 opaque id；任一输入变化使旧 id 失效。
- [ ] `CheckNowAsync` 可刷新/下载/验证但不改变 Activation、Desired State 或 LKG。
- [ ] `ApproveAsync` 要求当前登录 owner 有该本地 Instance 的权限，拒绝跨 owner、跨 Instance、过期、
  withdrawn、已消费或被替换 offer，并返回稳定结果。
- [ ] 批量“全部批准”只是逐 Instance 调用和呈现结果，不创建共享 approval/LKG 事务。
- [ ] Current 清楚区分 no update、checking、downloaded/awaiting approval、approved/staged、awaiting external
  reload、stability、succeeded、failed/rolled back 与 withdrawn action。
- [ ] approval audit 不记录 token/secret/私钥；AuthService JWT key 与 Registry signing key 无代码路径复用。
- [ ] Desktop UI 与 Headless API/CLI 都只依赖 management interface；System 不出现 Web update action。
- [ ] 授权、重放、并发 approve 与 restart persistence tests 通过。

## Dependencies

依赖 issue 03；遵循 ADR-043 的 local interactive authorization 边界。
