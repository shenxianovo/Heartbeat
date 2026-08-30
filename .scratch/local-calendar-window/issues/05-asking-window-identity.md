# 05 — Asking 窗口身份：问题缓存与提案提交不漂移

**What to build:** 当用户查看某天的问题并稍后提交 proposal 时，Daily Questions 的生成、缓存、读取与提交始终绑定问题最初产生时的完整 WindowKey。日期或浏览器时区已经变化的提交会被明确拒绝，不会把另一个事实窗口的证据写进知识层。

**Blocked by:** 04 — Recap 窗口身份：缓存、生成锁与 SSE.

**Status:** done

- [x] Daily Questions cache 以 Owner + WindowKey 唯一识别，并保留可诊断的 civil metadata 与 UTC bounds
- [x] question read/generation 使用验证后的 resolved Instant Window，不再从 DateTimeOffset 或 fixed duration 重建日窗
- [x] 每个返回给 Browser 的问题携带足以关联原 WindowKey 的稳定提交凭据，但 caller-supplied correlation key 不成为持久化真相
- [x] proposal submission 对原始 WindowKey 做严格绑定；窗口不存在、不属于 owner 或与当前提交上下文不匹配时稳定拒绝且不写知识数据
- [x] 同一 LocalDate 在不同 timezone/end/version 下分别缓存，不能共享问题或 proposal lookup
- [x] legacy question rows 保留但不能命中新 WindowKey，迁移和启动不进行 eager proposal/LLM generation
- [x] 集成测试覆盖读取、缓存复用、生成失败重试、跨 owner、跨窗口提交拒绝，以及成功提交仍能完成既有知识写回闭环

## Comments

- 2026-08-29：实现完成。Daily Questions 的生成、读取、缓存、recurrence 组装与 proposal lookup
  全部消费 Analytics 验证后的 `ResolvedCalendarWindow`；缓存保存完整诊断字段并以
  `(OwnerId, WindowKey)` 唯一识别。问题响应携带 Analytics submission key，Browser 用当前
  Calendar Context 提交，日期/时区漂移稳定返回 `question_window_mismatch`。
- TDD 证据：先观察到服务 seam 无法表达 23h/24h 同 LocalDate、proposal 无窗口凭据、Browser
  仍发送旧 `date` 的失败，再逐个实现到 green。真实 PostgreSQL/HTTP 覆盖 cache reuse、失败重试、
  owner/window 隔离、legacy miss、迁移保留、无提交路径再生成，以及既有 proposal → commit 闭环。
- 自动验证：`dotnet test Heartbeat.slnx --no-restore`（884 tests）、`npm test`（34 files / 237 tests）、
  `dotnet build Heartbeat.slnx --no-restore`（0 warnings / 0 errors）、`npm run build`、
  `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 与
  `git diff --check` 全部通过。
- Code review：Standards 与 Spec 双轴并行完成；typed proposal error、generated client transport、
  civil date locality、lifecycle 与注释漂移均已按评审修正；最终 Standards 0 findings / Spec 0 findings。
