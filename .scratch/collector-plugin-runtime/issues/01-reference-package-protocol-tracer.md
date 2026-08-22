# 01 — 本地参考 Package 跑通 Collector Protocol

**What to build:** 让 Hub 能从一个不可变的本地参考 Collector Package 创建稳定 Instance，并通过 InProcess Binding 完成 Activation、Stream 开启和 Segment Fact 交付。这个切片同时把 Manifest、Fact Schema Document 和协议状态机变成可执行契约，为其他真实 Collector 提供同一条接入路径。

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] 本地 Package 的 Manifest、Artifact、Fact Schema 与内容哈希能够被读取和严格校验；Collector Instance 永久绑定一个 PackageId 和一个 Subject，更换 PackageId 必须创建新 Instance。
- [ ] 参考 Collector 可以完成 hello、initialize、streams.open、ready，并把一个 Segment Fact 交给现有 Hub 缓冲；Hub 取得可恢复责任后才返回 ACK。
- [ ] StreamId 跨 Activation 稳定，同一 Stream 同时只有一个 writer；本期更新语义允许先停止旧 Activation，不预建双活热切换。
- [ ] 重传、duplicate、superseded、同 Revision 内容冲突、背压和消息级拒绝均有 transcript contract tests。
- [ ] Stream Gap 是 v1 必需的丢失披露机制；无法披露 Gap 的 Activation 不得被视为具备完整 Fact 交付能力。
- [ ] Fact Schema Document 明确 payload schema、FactKind 演进策略与可重复计算的 hash 输入；实现结果同步回现有两份 Draft 规范。
