# 04: WPF 插件管理页

Status: ready-for-agent

## Parent

[PRD](../PRD.md)

## What to build

在 WPF（本机采集层 UI）加一个采集器管理页，按 desktop/CONTEXT.md 的 Active/Deactivate 语义：

- **Active = 流量推断**：hub（`SegmentIngestService`）记录 per-source 最近接收时间与累计条数；管理页列出见过的 Source 及其活跃状态（最近 N 分钟内有流量 = Active）。无注册表、无心跳协议。
- **Deactivate = hub 拒收**：本地配置黑名单（随现有 Agent 配置持久化）；被停用 Source 的 POST 返回 403，段丢弃。管理页提供每个 Source 的启用/停用开关。
- 扩展侧把 403 理解为"被停用"：停止上报、退避并定期探询恢复——与 hub 不可达的退避是同一路径（issue 01 已建），本片只需确认 403 走该路径且不无限堆积本地缓冲。

## Acceptance criteria

- [ ] 扩展正常上报时，管理页显示 browser 采集器为 Active（含最近接收时间）
- [ ] 浏览器关闭一段时间后显示为不活跃（诚实反映"管道不通"）
- [ ] 停用 browser 后 hub 对其 POST 返 403，段不入缓冲；重新启用后数据恢复流入
- [ ] 黑名单跨 Agent 重启持久
- [ ] 被停用期间扩展退避、不无限膨胀本地缓冲
- [ ] hub 记账逻辑有单元测试（最近时间/计数/黑名单判定）

## Blocked by

- [01](./01-browser-extension-e2e.md)
