# Local Calendar Window Atomic Release

Local Calendar Window 是一个不可拆分的发布单元：Frontend、Analytics、数据库 schema 与
`frontend/src/api/client.ts` 必须来自同一个 commit。新旧任一方向的 Frontend / Analytics 混合
rollout 都不受支持；旧 fixed-offset 客户端也不能作为 fallback 保留。

## 发布前

1. 备份数据库，并记录可恢复点。
2. 从待发布 Analytics 的 Development `/openapi/v1.json` 用 NSwag 14.7.1 重新生成 Frontend
   client，确认生成后 `git diff` 为空。
3. 执行 Frontend typecheck、完整测试和生产 build，以及 Analytics/server 完整测试与 solution
   build。迁移测试必须证明 legacy Recap / Daily Questions rows 保留且不会命中新 WindowKey。
4. 确认发布物同时包含 `RecapWindowIdentity`、`AskingWindowIdentity` 两个迁移、Analytics binary、
   Frontend assets 与生成 client。

## 切换

1. 在不向用户流量暴露混合版本的维护窗口内应用迁移。
2. 部署同一 commit 的 Analytics 与 Frontend；两边全部 ready 后再恢复流量。
3. 分别 smoke owner / public 的 Daily Report、Weekly Report、Recap，以及 owner 的 Asking 与
   Recap 纠正。请求必须携带完整 day/week envelope；timezone 规则不一致必须返回
   `calendar_rules_mismatch`，不得 fallback。
4. 验证普通日与一个 DST transition 日的 Report、Timeline、Usage、Segments、Key Frequency、
   Recap 与 Asking 使用相同的 UTC endpoints。

## 回滚边界

- 恢复流量前可以把 Frontend 与 Analytics 一起回滚；不能只回滚其中一边。
- 新 schema 的 nullable 诊断列可以保留，但新 WindowKey 行一旦已写入，旧 Analytics 可能无法安全
  解释同一 WindowStart 下的多窗口数据。此时不得做 application-only rollback；应停止流量并从
  发布前备份恢复整个数据与应用单元，或先完成专门的数据协调。
- 不运行 Down migration 猜测重建旧 fixed-offset 身份，也不 eager 生成 legacy Recap / Questions。
