# Runbooks

低频或高风险操作按任务单独阅读：

- [Refresh Local Data](refresh-local-data.md) — 用服务器快照替换本地 E2E 数据库。
- [Local Data Smoke](local-data-smoke.md) — 用聚合不变量和前后水位验证历史数据与新客户端数据。
- [App Catalog Operations](app-catalog.md) — 修改、发布和诊断部署全局产品映射。
- [Reverse Proxy Chain](reverse-proxy.md) — 线上 Cloudflare → Caddy → nginx → backend 的超时与缓冲约束，长响应/流式故障排查。
- [Local Calendar Window Atomic Release](local-calendar-window-release.md) — Frontend、Analytics、schema 与生成 client 的不可拆分切换和回滚边界。

日常启动、运行和测试回到 [Development Guide](../development.md)。
