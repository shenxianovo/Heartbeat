# 09 — 深化 Collector Protocol Client 与持久交付

**What to build:** 把 C# Collector 中重复的生命周期、ACK/重试、Gap、授权、Collector Secret、drain 与文件 outbox 收进一个深 .NET Collector Protocol Client；stdio 与 InProcess 是该 seam 上的两个 Transport Binding adapter。跨语言稳定协议由语言无关的 conformance corpus 定义，Browser 与 .NET 都消费同一份契约。

**Blocked by:** 无。

**Status:** done

- [x] 模块 interface 只暴露 Collector observation 与协议交互，不泄漏具体 JSON 文件布局。
- [x] 文件格式使用根级 `schemaVersion`，未知版本与损坏文件有明确、可测试的隔离策略。
- [x] 写入使用同目录临时文件加原子替换；瞬时写失败不会丢失内存中的待确认项，并会在无新输入时继续重试。
- [x] FactId 在重放与显式 retry 时保持稳定；MessageId 标识单次投递 attempt，并在 retry/新 activation 时轮换。
- [x] 配额耗尽或损坏恢复造成不可恢复丢失时，由模块生成并持久化 Stream Gap。
- [x] Reference、VRChat 与 System 只保留领域 observation 或真实 adapter；通用协议测试集中在共享模块。
- [x] Browser Extension 不接入 .NET 文件实现，而与 .NET 消费同一份语言无关 conformance corpus。

## Comments

- 2026-08-27：Collector Activation Session 架构收口时识别出该重复 seam。本轮只统一私有 JSON 的 versioned envelope 规则，不把 outbox 重构混入协议生命周期变更。
- 2026-08-28：本地协议实现尚未交付，允许直接深化 seam。本轮建立独立 `Heartbeat.Collection.CollectorProtocol` deep module，把生命周期、持久交付、ACK/retry、Gap、授权、Secret 与 drain 收进模块；stdio 和 InProcess 两个 adapter 证明 interface 不依赖具体 transport。新增语言无关 conformance corpus，并由 .NET 与 Browser 测试共同消费。Reference 的 raw stdio 仅保留为 Hub 的对抗性 fixture。
