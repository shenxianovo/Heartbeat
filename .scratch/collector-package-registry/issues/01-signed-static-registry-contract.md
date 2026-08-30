# 01 — 定义并验证 signed static Registry contract

Status: ready-for-agent

Owner: Collection / Package Delivery

Priority: P1 — 后续发布和 Runtime 解析必须共享同一 canonical byte contract。

## What to build

定义版本化 channel/release metadata schema、canonical serialization、Ed25519 签名 envelope、路径布局
与跨 runtime golden fixtures。提供本地静态 fixture server，覆盖签名、hash、长度、不可变版本、撤回
与每 Package channel 行为。

## Acceptance

- [ ] `stable.json` 绑定 PackageId、channel revision、精确 release version/ref、published time 与签名；
  `releases/{version}.json` 绑定 PackageId、version、host/protocol compatibility、各平台 artifact 的 URL、
  length、SHA-256 与签名。
- [ ] canonical serialization 与 key id/algorithm/version 明确；未知版本、未知 key、坏签名、错 PackageId、
  错 version、同版本异 hash 全部得到稳定结构化错误。
- [ ] 读取路径禁止跨 origin/目录逃逸；artifact redirect policy 被明确测试，不能绕过 signed metadata。
- [ ] withdrawn release 不再形成新 offer，但现有 Installation 仍可离线打开；状态可供管理面显示。
- [ ] golden fixtures 同时由发布工具测试与 .NET verifier 测试消费，包含 tamper cases。
- [ ] 本地静态 server 证明 per-Package channel 可独立更新，另一个 Package 的 channel bytes 不变。
- [ ] glossary/ADR/schema docs 与 `git diff --check` 通过，证据追加在 Comments。

## Non-goals

不实现动态 Registry API、第三方上传或 owner approval。
