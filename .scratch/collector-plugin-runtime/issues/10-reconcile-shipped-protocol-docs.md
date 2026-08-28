# 10 — 对账已交付实现与 Protocol / Fact 规范

**What to build:** 把 PRD 与两份 `Draft 0.2` 中“实现前待裁决”的内容逐条对照当前代码、schema、conformance corpus 和 ADR，区分已经由实现裁决、仍有意留白、以及真实未完成的契约，恢复文档作为权威入口的可信度。

**Blocked by:** None.

**Status:** done

- [x] PRD 的十条“草案暴露的矛盾”逐条标记当前结论与证据，不把已经实现的 lease、capability、declaration、schema、AppHint、ExternalHost lease、Instance/Package 绑定继续写成实现前 blocker。
- [x] “必须覆盖的兼容场景”表与现有 transcript/runtime/conformance tests 对账，删除已经过时的“缺”标记，并区分当前覆盖与未来 Package Registry / multi-major / host-upgrade 能力；dimension 配额按产品裁决不承诺且不建 follow-up。
- [x] 旧 Collector Protocol §15 与 Fact Model §12 的已实现结论迁入 PRD、正式架构/ADR/context/contract 文档；未进入 v1 可执行面的 Measurement 与未来 Package Registry 继续明确留白。
- [x] 裁决 `Draft 0.2` 为实现前的一次性推导工具；长期事实迁移并验证后删除，不让 shipped runtime 继续依赖临时 prose spec。
- [x] 明确正式架构/ADR/context、`collection/contracts/facts/`、conformance corpus 与代码 validation 的权威关系，避免同一规则多处漂移。
- [x] README、系统架构、ADR-030/040/041 与本 PRD 的 current-state 叙述一致，相关本地链接和章节锚点有效。

## Comments

- 2026-08-28：完成 source-controlled 跨实现 JSON 契约命名对账；私有 runtime/outbox/cache/secret/browser state JSON 保持不变。System 与 Browser 的 observation declaration 统一由 Package 文件加载，旧 System 内嵌 JSON 已移除。
- 自动验证：Browser build + 77 tests；Collector Protocol 4 tests；System Collector 35 tests；Hub 204 tests；Windows 47 tests；macOS 78 tests；VRChat 4 tests；`collector-contracts.mjs check`（含 `--base-ref origin/main`）与 `git diff --check` 通过。
- Review：Standards / Spec 两轴审查发现的 descriptor ignore 问题已用精确 `.gitignore` 例外修复，并以 `git status` / `git check-ignore` 确认两个新 `*.artifact.json` 可跟踪；tracker 状态与验收项在同一变更 closeout。
