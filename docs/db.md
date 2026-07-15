# Heartbeat 数据库导读

持久化用 EF Core + PostgreSQL,**schema 的唯一真相源是实体类与迁移**——本文不再
维护逐字段 ER 图(手抄副本必漂移,旧版 ER 图里已删的 `Device.ApiKey` 列就是教训)。

## 真相源

| 想知道 | 去哪看 |
| --- | --- |
| 实体与字段 | `server/Heartbeat.Server/Entities/`(App / AppIcon / Device / User / InputEvent / ActivitySegment / Recap) |
| 关系、索引、约束 | `server/Heartbeat.Server/Data/AppDbContext.cs` |
| schema 演进历史 | `server/Heartbeat.Server/Migrations/`(迁移名即变更意图,如 `RemoveDeviceApiKey`) |
| 各实体的领域语义 | `shared/CONTEXT.md` Glossary(Device / App / ActivitySegment / Source / IdentityKey / InputEvent / Recap 等) |

迁移在启动时全环境自动应用(ADR-013)。

## 生成不出来的设计意图

- **多租户不焊死**:User 拥有多个 Device(`OwnerId` = JWT sub),业务查询在
  Service 层显式按 OwnerId 过滤——隔离是代码约定,不是 schema 强制(行级安全
  未启用)。设备身份 = (OwnerId, HardwareId),见 ADR-024。
- **ActivitySegment 是使用记录的泛化形态**(ADR-017/018):`AppUsage` 一词仅指
  "system source 的段"这一语义,不再是独立表形状。(Source, IdentityKey) 做
  upsert 的 identity guard,快照生长而非追加。
- **InputEvent 一行一事件,不聚合**(ADR-012):`Id` 为 Agent 生成的 UUIDv7,
  兼作去重键,离线重传幂等;`(DeviceId, Timestamp)` 索引支撑按设备+时间范围计数。
- **Recap 缓存按 (Owner, 日窗口) 落库**(ADR-023),是 LLM 生成结果的持久化,
  可随时重算。
