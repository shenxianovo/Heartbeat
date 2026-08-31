# 08 — 评估统一 InProcess 与 ExternalHost Ready publication transaction

Status: ready-for-agent

Owner: Collection / Hub Runtime

Priority: P3 — 当前两条路径的 prepare/store publish/writer grant 骨架近似，但尚未证明抽取能减少权威来源而
不抹平 ExternalHost 的 pending→active、applied revision 与弱 lease 差异。

## Acceptance

- [ ] 以现有 `CollectorActivationLifetime.PublishReadyAsync` 为唯一线性化 seam，对比两条路径真正相同与必须
  保留的 Driver/Binding 差异。
- [ ] 方案不扩大 public Interface、不引入跨进程 owner，也不以 callback/flag 参数堆形成浅层 helper。
- [ ] 只有在 durable state publication、schema registration、writer grant 与 pending commit removal 能由一个深
  transaction 明显减少权威来源时才实施；否则以代码证据关闭为 `wontfix`。
- [ ] 保持 wire shape、runtime store、Package/Instance/Activation 身份与 ExternalHost lease 语义兼容。

## Comments

### 2026-08-31 — third-round P3 review

`PrepareCollectorReady/TryCommitCollectorReady` 与
`PrepareExternalHostReady/TryCommitExternalHostReady` 共享 store prepare/publish、schema registration、writer grant
的形状；但 ExternalHost 同时承担 pending activation 转移、独立 applied revision 校验、starting-instance cleanup，
而 InProcess writer 描述符与 ready setup 不同。本轮五条 P1/P2 不变量不依赖消除此重复；立即抽取会把差异转成
多组 callback/条件参数，尚不能证明减少权威来源，因此按要求记录为非阻断 follow-up。
