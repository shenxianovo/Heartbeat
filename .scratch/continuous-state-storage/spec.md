# 持续状态的原子写入

实现 ADR-0002 的持久化部分，作为逐步重写的下一步。沿用现有四张表，不增加分轨字段、设备表、版本字段或永久结束标记。

## 范围

- 提供内部持续状态存储入口，接受已通过领域校验的完整 Record；调用方负责使用服务端时间生成 `received_at`，并确认该协议表达持续状态。
- 仅处理 `range + explicit` 的持续状态，不将任意明确区间开放为可修改记录。
- 沿 Track、Collector、Timeline 校验 Owner，未知或不属于该 Owner 的 Track 返回相同结果。
- 新 ID 插入记录；已有 ID 仅在 Track、起点、观察时间和 JSON 观测值一致时，原子取最大的结束时间。
- JSON 观测值按 PostgreSQL jsonb 值相等判断，允许对象属性顺序和空白不同。
- `received_at` 保留首次成功写入时的值；后续确认不作为修改固定观测字段的理由。
- 返回本次写入确认的结束时间与首次接收时间；固定字段冲突不修改已有记录。
- 同一 Track 可以存在重叠记录，各自按 Record ID 独立续期。

## 验证

通过真实 PostgreSQL 验证首次写入、重复与乱序请求、并发首次写入与续期、固定字段冲突、Owner 隔离、JSON 等价与重叠区间。

## 后续

Track 获取及具体协议注册、HTTP 上传、重放查询、Collector 本地持久队列和设备关联分步实现。本入口不解决历史纠错、删除后旧重传、TTL 或时钟异常。

## Comments

- 2026-09-12：内部存储入口及 PostgreSQL 原子写入已实现并注册到依赖注入。首次写入测试先确认因缺少实现而失败，再完成实现。新增 15 项集成测试；`dotnet test Heartbeat.slnx --no-restore` 全部通过：Domain 26/26、Integration 41/41，无 warning。未修改 EF 模型或 migration；未对本地业务数据库执行迁移。HTTP 上传与重放仍待后续步骤。
