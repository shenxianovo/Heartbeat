# 记录模型持久化

本目录将记录领域模型映射到 PostgreSQL。领域术语以根目录的 [`CONTEXT.md`](../../../../CONTEXT.md) 为准；模型选择的原因见 [`ADR-0001`](../../../../docs/adr/ADR-0001-time-ordered-observation-tracks.md)；完整结构见[记录存储模型](../../../../docs/recording-storage-model.md)。

## 映射约定

- 所有外键使用 `ON DELETE RESTRICT`。
- 业务 ID 均由应用生成 UUID v7，EF 使用 `ValueGeneratedNever`。
- Collector 的 `key`、`target` 和 `display_name` 使用 `varchar(255)`；其余不限定长度的字符串使用 PostgreSQL `text`。
- 所有时间使用 `timestamptz`；领域对象在创建时规范化为 UTC。
- `time_mode` 保存 `point` 或 `range`。
- Track 的 `time_mode` 只接受 `point` 和 `range`；Point 不带结束时间，Range 必须提供结束时间。
- `ended_at` 不得早于 `started_at`。它与 Track 时间模式的一致性由领域写入入口验证，数据库不使用跨表触发器。
- `observed_at` 为空表示 Collector 观察时间等于 `started_at`。
- Record 的 `value` 保存任意已定义的 JSON 值；后端不按 Track `(type, version)` 校验具体结构。
- 对象、Record 和关联由同一 SQL 原子写入；对象 namespace/key 的唯一约束在 Timeline 内生效。`records.objects` 保存原生声明，`objects` 最新名称和 `record_objects` 为其投影。
- `range` 通过单条 SQL 原子执行 `max(ended_at)`；冲突不改动固定字段，`received_at` 保留首次成功写入值。
- Track 获取与 Record 写入都在 SQL 中限制 Owner 归属。
- 批量上传逐条提交，可能部分成功；重试复用 Record ID。

## Migration

重写期间只维护 Initial migration 和 model snapshot，不增加增量 migration。

```bash
dotnet ef database update \
  --project src/Backend/Heartbeat.Infrastructure \
  --startup-project src/Backend/Heartbeat.Api
```

修改 EF 模型后，更新 Initial migration 和 snapshot，再检查是否仍有未迁移变更：

```bash
dotnet ef migrations has-pending-model-changes \
  --project src/Backend/Heartbeat.Infrastructure \
  --startup-project src/Backend/Heartbeat.Api
```
