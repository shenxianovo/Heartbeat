# 09 — 统一 Managed Collector Outbox 持久化

**What to build:** 把 System 与 ManagedProcess Collector 中重复的文件 outbox 机制收敛为一个可复用的持久化模块，统一 versioned envelope、原子替换、重放、dead-letter 与 Stream Gap 恢复语义，同时保持各 Collector 的 Fact 折叠逻辑独立。

**Blocked by:** 无。

**Status:** ready-for-agent

- [ ] 模块接口只暴露 enqueue、ack/remove、pending snapshot、dead-letter 与恢复结果，不泄漏具体 JSON 文件布局。
- [ ] 文件格式使用根级 `schemaVersion`，未知版本与损坏文件有明确、可测试的隔离或失败策略。
- [ ] 写入使用同目录临时文件加原子替换；瞬时写失败不会丢失内存中的待确认项，并会在无新输入时继续重试。
- [ ] 重启后按稳定 MessageId / FactId 重放，ACK 丢失不会生成新的业务身份。
- [ ] 配额耗尽或损坏恢复造成不可恢复丢失时，调用方能够发布 Stream Gap；模块本身不决定业务 Gap reason。
- [ ] System 与 VRChat 的现有恢复、backpressure、dead-letter 测试改为共享契约测试，Collector 只保留各自的适配测试。
- [ ] Browser Extension 的 `chrome.storage` queue 不强行接入文件实现；若复用，只复用与存储无关的 outbox 状态机契约。

## Comments

- 2026-08-27：Collector Activation Session 架构收口时识别出该重复 seam。本轮只统一私有 JSON 的 versioned envelope 规则，不把 outbox 重构混入协议生命周期变更。
