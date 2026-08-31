# 05 — 三种 Execution Driver conformance

Status: done

Owner: Collection / Execution Drivers

Priority: P1 — 共享生命周期语义不能抹平真实执行能力差异。

## Acceptance

- [x] InProcess 证明 bounded drain、deadline fence 与 no late durable mutation。
- [x] ManagedProcess 证明 protocol drain、到期 terminate、supervision/update 共用 terminal result。
- [x] ExternalHost 证明 revoke/lease 语义，不伪装强制停止外部代码。
- [x] 三者通过同一 lifecycle conformance vocabulary；Transport Binding 不改变协议语义。

## Comments

### 2026-08-31 — adapter conformance complete

实际 transcript 场景现在把 Terminal execution 与 conformance corpus 一起断言，而非只比较硬编码字符串：
InProcess deadline 是 `InProcessFencedExecution`；ManagedProcess deadline 是
`ManagedProcessTerminatedExecution(DeadlineExceeded)`，wire deadline 与 Terminal deadline 完全相同且 drain
只写一次；ExternalHost 是携带 `HostReported / NotReported` evidence 的
`ExternalHostLeaseRevokedExecution`，永不声明 host terminated。Runtime Dispose/public Stop、update/supervision、
lease expiry/replacement/desired-state 都只提交 Intent 并共享同一 Terminal。
