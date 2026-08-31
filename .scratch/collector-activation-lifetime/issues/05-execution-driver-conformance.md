# 05 — 三种 Execution Driver conformance

Status: ready-for-agent

Owner: Collection / Execution Drivers

Priority: P1 — 共享生命周期语义不能抹平真实执行能力差异。

## Acceptance

- [ ] InProcess 证明 bounded drain、deadline fence 与 no late durable mutation。
- [ ] ManagedProcess 证明 protocol drain、到期 terminate、supervision/update 共用 terminal result。
- [ ] ExternalHost 证明 revoke/lease 语义，不伪装强制停止外部代码。
- [ ] 三者通过同一 lifecycle conformance vocabulary；Transport Binding 不改变协议语义。

