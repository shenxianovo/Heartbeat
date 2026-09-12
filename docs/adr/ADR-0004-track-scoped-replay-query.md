# ADR-0004：按 Track 提供最小重放查询

## 状态：已接受

## 日期：2026-09-12

[`3ca4aed`](https://github.com/shenxianovo/heartbeat/commit/3ca4aed9d149318f679aa9891f8ff0ff03a9fdb2) — feat(recording): add replay queries and independent macOS collection

## 背景

注册 Collector、获取 Track、前台应用协议和批量上传已经形成写入链路，但还缺少读取入口来确认 Record 可以按时间重放。此时若直接设计 Timeline 级聚合、分页游标、设备维度筛选或应用身份解析，会把尚未确认的展示和分析需求提前固化到记录内核。

现有存储规范只承诺 `(track_id, started_at, id)` 的稳定顺序索引，不为 `received_at`、`observed_at` 或 `ended_at` 提前建索引。最小重放查询应先贴合这一约束，让桌面 Collector 能完成后端闭环。

## 决策

先提供 Track 级重放查询：调用方指定一个已存在且属于当前 Owner 的 Track，可选提供 `from`、`to` 和 `limit`。查询返回 Track 元信息和按 `(started_at, id)` 排序的 Record 列表，Record 保留存储中的时间字段和协议 `value`。

时间窗采用 `[from, to)`。有 `ended_at` 的 Record 以区间交叠纳入窗口；没有 `ended_at` 的 Record 以 `started_at` 纳入窗口。查询不跨 Track 聚合，不解析 Application Identity，不按设备分组，不增加分轨字段、设备表或通用 metadata。

当前查询返回已存 `ended_at`，不计算 `range + next_record` 的派生结束时间。该模式的重放语义等出现实际协议后再单独设计。

## 后果

- ✅ 写入后的 Record 可以通过受 Owner 保护的 HTTP 入口按 Track 稳定取回。
- ✅ 接口很小，读取归属校验、窗口过滤和排序集中在一个 Application 用例和一个 PostgreSQL adapter 中。
- ✅ 不改变四层记录模型，不新增设备表、分轨字段或提前优化用的索引。
- ⚠️ 调用方需要先知道 Track ID；Timeline 级重放和跨 Track 合并仍未实现。
- ⚠️ `range + next_record` 的派生结束时间尚未通过该查询表达。

## 参考

- [`CONTEXT.md`](../../CONTEXT.md) — 领域术语。
- [`docs/recording-storage-model.md`](../recording-storage-model.md) — 存储结构、索引和重放约束。
- [`docs/recording-api.md`](../recording-api.md) — HTTP 契约。
- [`src/Backend/Heartbeat.Application/Recording/ReplayRecords.cs`](../../src/Backend/Heartbeat.Application/Recording/ReplayRecords.cs) — 重放用例与端口。
- [`src/Backend/Heartbeat.Infrastructure/Persistence/PostgresRecordReplayStore.cs`](../../src/Backend/Heartbeat.Infrastructure/Persistence/PostgresRecordReplayStore.cs) — PostgreSQL 查询 adapter。
