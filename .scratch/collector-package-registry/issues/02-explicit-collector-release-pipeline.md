# 02 — 建立 VRChat 显式 tag release pipeline

Status: needs-triage

Owner: Build / Release

Priority: P1 — 第一条纵切必须证明 VRChat 不再依赖 Headless/Desktop 一起发布。

## What to build

让 VRChat 只在自己的显式 SemVer tag 上构建当前真实运行平台的 artifact，生成 index 所需的 Version、
URL、length 与 SHA-256 并发布到静态 Web 目录。普通 main CI 只做 dry-run，不发布用户可见候选。

## Acceptance

- [ ] `collector-vrchat/vX.Y.Z` 是唯一发布 trigger；错误 package/tag、非 SemVer 或 dirty generated contract
  使 pipeline fail closed。
- [ ] artifact 可独立运行并带 Package manifest；pipeline 计算 length/hash 并生成 issue 01 的 index entry。
- [ ] artifact 是当前 Headless 实际环境可运行的 framework-dependent VRChat zip；不生成 self-contained 或
  多平台矩阵。
- [ ] artifact 先上传并可读，随后才替换 index；index 不得指向尚不存在的文件。
- [ ] 已存在的 Version 不得覆盖；同 tag 重跑只有在远端 artifact 与 index 完全一致时才幂等，否则失败。
- [ ] main/PR workflow 以临时目录完成端到端 dry-run，不写部署中的 Registry。
- [ ] System Collector 明确排除在 Web release matrix 外。
- [ ] Browser、签名、key rotation、撤回与多平台矩阵不进入本 issue。

## Dependencies

与 issue 01 并行开发，但合并前必须消费其最终 schema/fixtures。
