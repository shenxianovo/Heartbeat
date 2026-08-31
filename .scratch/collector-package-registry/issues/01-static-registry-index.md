# 01 — 定义最小 per-Package Registry index

Status: ready-for-agent

Owner: Collection / Package Delivery

Priority: P1 — 发布工具与 Runtime 必须对候选身份、文件长度和 hash 使用同一 contract。

## What to build

定义版本化的 per-Package `current.json` schema，列出 PackageId、Version、artifact URL、length 与
SHA-256。提供本地
fixture server，让发布工具与 .NET Runtime 消费同一份正常和损坏样本。MVP 不包含签名、channel、撤回或
SemVer solver。

## Acceptance

- [ ] `/packages/{packageId}/current.json` 有 schema version，并提供精确 PackageId、Version、artifact URL、
  length 与 SHA-256；artifact 位于 `/packages/{packageId}/versions/{version}/`。
- [ ] 缺字段、未知 schema、重复 PackageId、非法 Version/URL/hash/length 返回稳定结构化错误。
- [ ] artifact URL 只允许 Registry 同 origin、指定目录内的 HTTPS 路径；redirect 不得绕过该边界。
- [ ] fixture 同时由发布工具测试与 .NET reader 测试消费，覆盖错长度、错 hash 与损坏 Package。
- [ ] 本地静态 server 证明 Runtime 可以读取 index 并下载一个 VRChat artifact。
- [ ] glossary/ADR/schema docs 与 `git diff --check` 通过，证据追加在 Comments。

## Non-goals

不实现 Ed25519、canonical signature bytes、channel、withdrawn、动态 Registry API、第三方上传或 owner approval。
不复制 host version、protocol 或 capability compatibility matrix；这些由 Package loader 与真实握手判定。
