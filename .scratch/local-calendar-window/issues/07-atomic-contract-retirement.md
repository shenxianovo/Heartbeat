# 07 — 原子收口：退休旧契约并执行发布验证

**What to build:** Local Calendar Window 的所有消费者完成迁移后，彻底移除旧 fixed-offset、可选日期和固定 24h/7×24h 日历路径，并把 Frontend、Analytics、缓存身份、OpenAPI 与生成客户端收成一个不可拆分的发布单元。最终仓库只存在一套“这天 / 这周”语义，并有证据证明可安全发布。

**Blocked by:** 06 — 整页刷新一致性：不可变 Context 与过期响应隔离.

**Status:** ready-for-agent

- [ ] 所有 calendar endpoint 都要求版本化 Local Calendar Window envelope；旧单 DateTimeOffset、可选日期、UTC-now fallback 与 fixed-offset calendar contract 已删除
- [ ] calendar consumers 不再调用固定 24h/168h helper 或手写毫秒加法；保留的通用 Instant Window 与 DateOnly 模块有清晰独立边界
- [ ] Analytics 不存在 dual-read、fallback 或无退出条件 compatibility path，legacy cache 仅作为不会命中新 key 的历史派生数据保留
- [ ] 从本地 Development OpenAPI 重新生成 Frontend client，生成结果与手写 Recap SSE adapter 对 Local Calendar Window 的编码一致
- [ ] 搜索与接口级测试证明 Report、Recap、Asking、Timeline、Usage、Segments、Key Frequency、owner/public 均已迁移且没有遗漏调用方
- [ ] 数据库迁移可从现有 schema 正向应用，legacy rows 保留，无 eager LLM backfill，WindowKey 唯一性和诊断字段正确
- [ ] Frontend typecheck、完整测试、生产 build、Analytics/server 完整测试与 solution build 全部通过
- [ ] 发布说明明确这是 Frontend + Analytics + schema/client 的原子发布，不支持新旧任一方向的混合 rollout
