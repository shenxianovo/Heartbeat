# 04 — Recap 窗口身份：缓存、生成锁与 SSE

**What to build:** 当用户读取或生成某个本地日期的 Recap 时，普通读取、公开读取、生成流、投影、freshness 判断、缓存和互斥锁都绑定同一个经 Analytics 验证的窗口。相同本地日期在不同时区不会复用错误叙事，旧 fixed-offset 缓存也只会自然 miss 后按需重建。

**Blocked by:** 01 — 日报主射弹：Calendar Context 到严格验证.

**Status:** ready-for-agent

- [ ] Analytics 从验证后的规范化表示生成持久 WindowKey，身份至少包含版本、kind、LocalDate、timezone、UTC start 与 end-exclusive
- [ ] Recap 持久化保存 WindowKey、civil metadata 与完整 UTC bounds，并以 Owner + WindowKey 唯一识别缓存
- [ ] Recap read、public cached read、generation、projection、staleness 与 generation lock 全部消费同一个 resolved Instant Window / WindowKey
- [ ] Browser 的普通 Recap transport 与手写 SSE transport 编码同一个 Calendar Context，且调用方 correlation identity 不参与持久化身份
- [ ] 相同规范窗口的并发生成收敛到同一把锁；不同 timezone、end、kind 或 version 的窗口不会互相阻塞或碰撞
- [ ] 数据迁移保留 legacy fixed-offset rows，但这些行不能命中新 WindowKey；迁移或启动过程不触发 eager LLM generation
- [ ] owner/public、23/24/25 小时日、缓存命中、失败保留 last-good、知识 staleness 与重新生成路径均有集成覆盖
