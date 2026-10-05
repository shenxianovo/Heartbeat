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
- `objects` 保存全应用 UUIDv7 与 Owner；Timeline、Collector、Track、Record、Hub 的主键均是对象外键。来源仍沿 Record → Track → Collector → Timeline 确定。
- `object_bindings` 保存识别地址，唯一约束为 `(owner_id, scope_id, namespace, key)`，空 scope 也参与唯一约束。一个对象可以有多个地址；scope 是另一个对象的 ID。
- `records.objects` 保存原始声明；`record_objects` 按引用序号保存解析结果，`object_descriptions` 保存当前名称。名称与识别映射分别维护。
- `PostgresObjectDiscovery` 负责公共引用解析，不认识具名 Collector。Record 存储负责事务，记录写入、发现、关联与描述整条提交或回滚。
- Timeline、Collector、Track 注册先确定最终采用的 ID，再保存其身份。重复与并发请求不会保存未使用的候选身份。
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
