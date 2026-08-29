# 07 — 原子收口：退休旧契约并执行发布验证

**What to build:** Local Calendar Window 的所有消费者完成迁移后，彻底移除旧 fixed-offset、可选日期和固定 24h/7×24h 日历路径，并把 Frontend、Analytics、缓存身份、OpenAPI 与生成客户端收成一个不可拆分的发布单元。最终仓库只存在一套“这天 / 这周”语义，并有证据证明可安全发布。

**Blocked by:** 06 — 整页刷新一致性：不可变 Context 与过期响应隔离.

**Status:** ready-for-human

- [x] 所有 calendar endpoint 都要求版本化 Local Calendar Window envelope；旧单 DateTimeOffset、可选日期、UTC-now fallback 与 fixed-offset calendar contract 已删除
- [x] calendar consumers 不再调用固定 24h/168h helper 或手写毫秒加法；保留的通用 Instant Window 与 DateOnly 模块有清晰独立边界
- [x] Analytics 不存在 dual-read、fallback 或无退出条件 compatibility path，legacy cache 仅作为不会命中新 key 的历史派生数据保留
- [x] 从本地 Development OpenAPI 重新生成 Frontend client，生成结果与手写 Recap SSE adapter 对 Local Calendar Window 的编码一致
- [x] 搜索与接口级测试证明 Report、Recap、Asking、Timeline、Usage、Segments、Key Frequency、owner/public 均已迁移且没有遗漏调用方
- [x] 数据库迁移可从现有 schema 正向应用，legacy rows 保留，无 eager LLM backfill，WindowKey 唯一性和诊断字段正确
- [x] Frontend typecheck、完整测试、生产 build、Analytics/server 完整测试与 solution build 全部通过
- [x] 发布说明明确这是 Frontend + Analytics + schema/client 的原子发布，不支持新旧任一方向的混合 rollout
- [ ] Maintainer 在本地 Dashboard 完成最终功能验收

## Comments

- 2026-08-29：实现与自动验证完成。最后的 fixed-offset consumer（Recap 纠正）已改为完整 day
  envelope；Analytics 先验证再把同一个 `ResolvedCalendarWindow` 交给 digest，旧
  `DateRange.Day(DateTimeOffset)` 与对应低层测试已删除。通用 `DateRange` 只保留半开 UTC
  Instant Window 语义，Episode / Strand DateOnly 未改动。
- OpenAPI / client：重建本地 Development backend 后从 `/openapi/v1.json` 用 NSwag 14.7.1
  重新生成 `frontend/src/api/client.ts`。新增 transport / HTTP / OpenAPI 测试证明 Recap 纠正与
  Report、Recap、Asking 一样要求六字段 envelope，缺失或 rules mismatch 在事实查询 / LLM 前拒绝；
  手写 Recap SSE 继续使用同一编码。
- TDD 证据：HTTP/OpenAPI 与 Frontend transport 先对旧 DateTimeOffset contract 变红，再迁移到
  envelope；随后用挂起 proposal / commit 复现 refresh generation 已变化时旧纠正结果覆盖新页面、
  以及错误重生成新日期的问题，再实现为提交后始终生成原 Calendar Context 且不污染新页面。
- 数据与发布：Recap / Daily Questions 迁移定向测试覆盖 existing schema 正向应用、legacy row 保留、
  新 WindowKey miss、唯一性与无 eager generation；`docs/runbooks/local-calendar-window-release.md`
  明确 Frontend + Analytics + schema/client 原子发布和整单元回滚边界。
- 自动验证：review 修复后重新执行 `npm test`（38 files / 257 tests）、`npm run build`、
  `dotnet test Heartbeat.slnx --no-restore`（882 tests）、`dotnet build Heartbeat.slnx --no-restore`
  （0 warnings / 0 errors）、`dotnet format style ... IDE1006 --verify-no-changes`、聚焦
  `vue-tsc -b` 与 `git diff --check` 全部通过。等待 maintainer 在本地 Dashboard 验收后再置 `done`。
- Code review：Standards 初审的重复 calendar error mapper 与测试命名均已修正；Spec 初审及复审
  找到的两个 refresh 时序均补了 red test——原窗口生成不会被新页面取消，旧窗口后台生成也不会
  取消或回写新页面。最终 Standards / Spec 复审均为 0 findings。
- 发布验证：本地 `npm run build` 通过后，实际 Frontend 镜像构建暴露了 Docker context 不包含共享
  calendar golden fixture 的发布阻断。现已把 Frontend Dockerfile、local compose 与 deploy workflow
  统一为仓库根 context，只复制 Frontend 与单一事实源 fixture；镜像重建成功，最终 Frontend +
  Analytics 容器已更新，`/` 与 `/openapi/v1.json` smoke 均返回 200。
- 2026-08-29 验收反馈：Dashboard 顶部重复的日期选择器与时区看板已合并为一个可点击的 Local
  Calendar Window 控件，完整显示 `日期 · IANA 时区 · UTC offset`，原日期选择交互保持不变；完整
  Frontend 验证更新为 38 files / 258 tests，生产构建与实际页面桌面/窄屏检查通过。
