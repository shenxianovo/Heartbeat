# 10 — 对账已交付实现与 Protocol / Fact 规范

**What to build:** 把 PRD 与两份 `Draft 0.2` 中“实现前待裁决”的内容逐条对照当前代码、schema、conformance corpus 和 ADR，区分已经由实现裁决、仍有意留白、以及真实未完成的契约，恢复文档作为权威入口的可信度。

**Blocked by:** None.

**Status:** ready-for-agent

- [ ] PRD 的十条“草案暴露的矛盾”逐条标记当前结论与证据，不把已经实现的 lease、capability、declaration、schema、AppHint、ExternalHost lease、Instance/Package 绑定继续写成实现前 blocker。
- [ ] “必须覆盖的兼容场景”表与现有 transcript/runtime/conformance tests 对账，删除已经过时的“缺”标记，并为真实缺口建立明确 follow-up。
- [ ] Collector Protocol §15 与 Fact Model §12 的未决项按当前实现重写；未进入 v1 可执行面的 Measurement 与未来 Package Registry 约束继续明确留白。
- [ ] 裁决 `Draft 0.2` 的身份：定稿为已实现的 v1 规范、继续作为包含未来内容的设计 draft，或拆成 implemented profile + future design；不能让 shipped runtime 依赖一份自称“实现前 blocker”的文档。
- [ ] 明确 prose spec、`collection/contracts/facts/`、conformance corpus 与代码 validation 的权威关系，避免同一规则多处漂移。
- [ ] README、系统架构、ADR-040/041 与本 PRD 的 current-state 叙述一致，且相关链接与章节锚点有效。
