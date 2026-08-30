# 02 — 建立每个 Collector 的显式 canonical release pipeline

Status: ready-for-agent

Owner: Build / Release

Priority: P1 — 独立发布不能依赖手工拼 manifest 或覆盖 mutable version。

## What to build

让每个非 BuiltIn 官方 Collector 只在自己的显式 SemVer tag 上构建平台矩阵，由单一 assembly job 验证
后生成 canonical release、签名并按 blob → release → channel 顺序发布。普通 main CI 只构建验证，不
发布 stable。

## Acceptance

- [ ] Browser 与 VRChat 分别有明确 tag trigger；错误 package/tag、非 SemVer、重复版本或 dirty generated
  contract 使 pipeline fail closed。
- [ ] 平台 job 输出带 provenance 的 artifact；assembly job 要求完整预期矩阵并拒绝重名/缺失/额外
  artifact。
- [ ] assembly 生成一次 canonical release metadata，计算 length/hash，并使用专用 Ed25519 CI secret
  签名；日志和 artifact 不泄露私钥。
- [ ] publish 顺序保证 channel 永远不指向尚不可读或未验证的 release/blob；重跑同内容幂等，同版本
  异内容失败。
- [ ] main/PR workflow 以测试 key 和临时目录完成端到端 dry-run，不写 production Registry。
- [ ] System Collector 明确排除在 Web release matrix 外。
- [ ] runbook 说明 key rotation、失败恢复、撤回发布和 production approval；真实 secret provisioning
  保留到 issue 07 人工 gate。

## Dependencies

与 issue 01 并行开发，但合并前必须消费其最终 schema/golden fixtures。
