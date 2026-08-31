# 04 — Client 侧显式 Delivery Ownership

Status: ready-for-agent

Owner: Collection / Collector Protocol Client

Priority: P1 — handoff、cancellation 与 persistence failure 当前共享异常控制流。

## Acceptance

- [ ] background → drain → fenced 由显式 delivery ownership/lease 表达。
- [ ] drain transition 一次线性化完成 admission close、ownership transfer、epoch advance、deadline capture。
- [ ] Superseded 是领域 outcome，不由 `OperationCanceledException` catch filter 表达。
- [ ] caller/deadline cancellation、persistence、Stop 与 completion failure 保持可区分。
- [ ] Fact/Gap、cooperative/deadline/failure 使用同一状态模型。
- [ ] observer/barrier 测试迁移到 Module Interface，test-only production observer 删除。

