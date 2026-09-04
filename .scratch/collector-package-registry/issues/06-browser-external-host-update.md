# 06 — 通用 ExternalHost 接入与身份级 Activation 所有权

Status: ready-for-agent

Owner: Collection / Collector Host Runtime

Priority: P1 — Browser 恢复接入前必须先让共享 Runtime 正确承载一个 Instance 下的多个 External Host；
不得用 Browser 专属 handler 绕过这一步。

## What to build

在 `Heartbeat.Collection.Hub` 中实现不含具名 Collector 的 ExternalHost 纵切：

1. 用一个通用 loopback 路由承载 Collector Protocol。`hello` 携带精确 Package/Artifact 身份、
   `externalHostIdentity`、`appIdentityKey` 与协议能力；handler 从 Installation 和 Runtime-owned 默认 Instance
   解析连接，不接受 URL、Package 特例或 source 级注册表。
2. 把 ExternalHost 的并发所有权从“整个 Collector Instance”缩小到 External Host Identity 与它打开的
   Fact Stream。不同 identity 可以在同一 Instance 下并行；同 identity 重连只替换自己的旧 Activation。
3. Stream 使用 Package 声明的 `appIdentityKey + externalHostIdentity` identifying dimensions。相同 identity
   改报不同 `appIdentityKey` 时拒绝并返回结构化身份冲突；正常重连复用原 Stream。
4. Segment 通用投影直接消费 `appIdentityKey` dimension；删除 `ICollectorAppHintResolver`、Null adapter 与
   Host 侧 `appHint` 解析。
5. Installation 与 Instance 已存在但没有 ExternalHost Activation 时，管理读模型报告
   `WaitingForExternalHost`；这不是 Activation failure。
6. 完整卸载先撤销该 Instance 下全部 pending/active ExternalHost Activation，再走 Runtime-owned Instance、
   Secret、data 与 Installation 删除；后续连接得到 `package_not_installed`。

## Acceptance

- [ ] 通用 ExternalHost route、handler、DTO 与组合入口不包含 Browser、Chrome、Edge、VRChat 或其他具名
      Collector；默认未安装连接只被拒绝，不影响 Host 启动。
- [ ] `hello` 必须匹配一份已验证 Installation 中当前平台唯一的 `externalHost` Artifact，以及该 Package 的
      Runtime-owned 默认 Instance；错 Package/version/artifact/hash 均 fail closed。
- [ ] 两个不同 `externalHostIdentity` 可以在同一个 Collector Instance 下同时 Ready，并分别写入自己的
      Fact Stream；现有 Instance 级 `stream_writer_conflict` 前置检查被移除。
- [ ] 同 identity 的新 Activation 以 `LeaseReplaced` 停止旧 Activation、取得原 Stream writer lease，且不
      停止其他 identity；并发重连有确定的单 owner 结果。
- [ ] Runtime 持久化或可从持久 Stream identity 判定 `externalHostIdentity → appIdentityKey` 绑定；同 identity
      改 App Key 被拒绝，Host restart 后仍不能静默改绑。
- [ ] `appIdentityKey` 作为通用 Stream dimension 进入 ActivitySegment 投影；Backend 不认识该 Key 时沿既有
      provisional App 路径保留事实，不要求 Host 产品映射。
- [ ] `ICollectorAppHintResolver`、`CollectorAppHintResolution`、Null adapter 及相关组合测试完整删除；
      `appHint` 不再是新 ExternalHost wire/Stream contract。
- [ ] 已安装无连接时状态为 `WaitingForExternalHost`；有 N 个 Ready identity 时通用快照能给出连接数；身份
      冲突使用稳定 failure code，普通状态文案不进入 Runtime。
- [ ] `RemoveInstanceAsync` 能停止并等待该 Instance 下全部 ExternalHost lifetime 完成后删除；失败时不伪造
      已卸载，成功后旧客户端重连得到 `package_not_installed`。
- [ ] 使用 Reference ExternalHost fixture 覆盖未安装、错精确身份、两个 identity 并行、同 identity 替换、
      identity/App 冲突、restart 后重连、卸载中重连与卸载后重连。
- [ ] 既有 InProcess、ManagedProcess 与 ExternalHost Collector Protocol conformance 全绿；生产 Host/Hub
      目录的具名 Collector 消融搜索继续通过。

## Non-goals

- 不增加 Desktop Marketplace UI；由 issue 10 承接。
- 不修改 Browser Collector 或发布 Browser Package；由 issue 07 承接。
- 不实现 Package 更新、热切换、LKG、自动重连策略、签名或第三方市场。
- 不定义 `manualSetup`/`manualRemoval` schema，不提供打开目录或执行 Package 命令的能力。

## Dependencies

- [ADR-049](../../../docs/adr/049-named-optional-collectors-outside-host-composition.md)：Host 不认识具名可选 Collector。
- [ADR-050](../../../docs/adr/050-generic-collector-marketplace-and-runtime-owned-instances.md)：Installation、默认
  Instance 与 Runtime 权威。
- [ADR-051](../../../docs/adr/051-generic-external-host-identity-and-browser-delivery.md)：本 issue 的身份与所有权决策。
- [issue 01](./01-static-registry-index.md)：共享 Marketplace/Installation 已存在。

## Comments

### 2026-09-04 — 重写旧 approval/LKG 规格

旧 issue 06 要求 Browser approved offer、side-by-side reload、exact-ready promotion 与 LKG rollback；这些能力
已被 ADR-048/050 明确移出开发期范围。Owner 重新 grill 后确认：一份 Browser Package 只创建一个
Machine-scoped Instance，不同浏览器/Profile 以 External Host Identity 并行；Collector 直接提供
`appIdentityKey`，Host 不解释 App Hint。本 issue 只建设可被任何 ExternalHost Collector 复用的 Runtime 与
protocol binding。
