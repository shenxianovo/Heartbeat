# 04 — Recap 窗口身份：缓存、生成锁与 SSE

**What to build:** 当用户读取或生成某个本地日期的 Recap 时，普通读取、公开读取、生成流、投影、freshness 判断、缓存和互斥锁都绑定同一个经 Analytics 验证的窗口。相同本地日期在不同时区不会复用错误叙事，旧 fixed-offset 缓存也只会自然 miss 后按需重建。

**Blocked by:** 01 — 日报主射弹：Calendar Context 到严格验证.

**Status:** done

- [x] Analytics 从验证后的规范化表示生成持久 WindowKey，身份至少包含版本、kind、LocalDate、timezone、UTC start 与 end-exclusive
- [x] Recap 持久化保存 WindowKey、civil metadata 与完整 UTC bounds，并以 Owner + WindowKey 唯一识别缓存
- [x] Recap read、public cached read、generation、projection、staleness 与 generation lock 全部消费同一个 resolved Instant Window / WindowKey
- [x] Browser 的普通 Recap transport 与手写 SSE transport 编码同一个 Calendar Context，且调用方 correlation identity 不参与持久化身份
- [x] 相同规范窗口的并发生成收敛到同一把锁；不同 timezone、end、kind 或 version 的窗口不会互相阻塞或碰撞
- [x] 数据迁移保留 legacy fixed-offset rows，但这些行不能命中新 WindowKey；迁移或启动过程不触发 eager LLM generation
- [x] owner/public、23/24/25 小时日、缓存命中、失败保留 last-good、知识 staleness 与重新生成路径均有集成覆盖

## Comments

- 2026-08-29：实现完成。Analytics 严格验证 day envelope 后生成强类型 `CalendarWindowKey`；owner/public 读取、SSE 生成、投影、freshness、持久化与生成锁共享同一 resolved window。迁移保留 legacy row 且不回填新身份，旧缓存只会自然 miss。
- TDD 证据：先观察到同窗口刷新误 abort、GET/SSE calendar mismatch 诊断丢失的失败测试，再实现窗口身份级取消与结构化错误保留；HTTP 集成测试证明同一规范窗口并发返回 409，而另一个有效窗口可同时进入生成。
- 自动验证：`dotnet test Heartbeat.slnx --no-restore`（876 tests）、`npm test`（29 files / 213 tests）、`dotnet build Heartbeat.slnx --no-restore`（0 warnings / 0 errors）、`npm run build`、`dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 与 `git diff --check` 全部通过。
- Code review：Standards 与 Spec 两轴并行完成；lifecycle 证据、WindowKey provenance、Calendar Window 比较局部性、同窗口刷新取消、稳定 mismatch 诊断与 HTTP 并发锁覆盖均已按评审结果修正；最终复审 Standards 0 findings / Spec 0 findings。
