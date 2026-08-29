# 05 — Asking 窗口身份：问题缓存与提案提交不漂移

**What to build:** 当用户查看某天的问题并稍后提交 proposal 时，Daily Questions 的生成、缓存、读取与提交始终绑定问题最初产生时的完整 WindowKey。日期或浏览器时区已经变化的提交会被明确拒绝，不会把另一个事实窗口的证据写进知识层。

**Blocked by:** 04 — Recap 窗口身份：缓存、生成锁与 SSE.

**Status:** ready-for-agent

- [ ] Daily Questions cache 以 Owner + WindowKey 唯一识别，并保留可诊断的 civil metadata 与 UTC bounds
- [ ] question read/generation 使用验证后的 resolved Instant Window，不再从 DateTimeOffset 或 fixed duration 重建日窗
- [ ] 每个返回给 Browser 的问题携带足以关联原 WindowKey 的稳定提交凭据，但 caller-supplied correlation key 不成为持久化真相
- [ ] proposal submission 对原始 WindowKey 做严格绑定；窗口不存在、不属于 owner 或与当前提交上下文不匹配时稳定拒绝且不写知识数据
- [ ] 同一 LocalDate 在不同 timezone/end/version 下分别缓存，不能共享问题或 proposal lookup
- [ ] legacy question rows 保留但不能命中新 WindowKey，迁移和启动不进行 eager proposal/LLM generation
- [ ] 集成测试覆盖读取、缓存复用、生成失败重试、跨 owner、跨窗口提交拒绝，以及成功提交仍能完成既有知识写回闭环
