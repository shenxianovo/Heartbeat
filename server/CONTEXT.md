# Analytics

接收采集数据，以快照 upsert 持久化，聚合报表，向 Dashboard 提供只读 API。

## Language

**Ingest（摄入）**:
统一摄入例程（ADR-018）：校验 → App 关联 → 按 Id 快照 upsert。两条入口：`/usage`（system 采集器，服务端代算 IdentityKey）与 `/segments`（插件段）。摄入可交换、可重入——乱序重传与批内同 Id 快照收敛到同一行。
_Avoid_: Merge、续接（ADR-001 的服务端合并已被 ADR-018 快照 upsert 取代；CanMerge 一词只存在于历史 ADR 中）

**Snapshot Upsert（快照 upsert）**:
Id 即活动身份：已有行则单调生长（EndTime 取 max、attributes 后写胜），新 Id 插入。采集端对同一活动多次上报的是同一 Id 的更大快照，不是新段。

**Report（报表）**:
对某 Owner 某时间窗的聚合视图（daily / weekly）。只消费 system 段——互斥轨，时长可求和；插件段只进回放，不进统计（ADR-017 §4 统计边界）。跨窗段做区间重叠 + 裁剪：只计落在窗口内的部分，不漏不双计（ADR-018 §4）。
_Avoid_: Statistics, Summary

**App**:
归一化的应用记录（进程名唯一），摄入时由 AppName 提示关联或创建。AppIcon 由 Agent 单独上报，挂在 App 下。App 是统计聚合的主维度。

**Owner / Device**:
数据隔离的两级键：所有查询以 `Device.OwnerId` 过滤（多用户就绪，见 CONTEXT-MAP 定位不变量 2）；Device 是一台采集来源机器，报表可按 Device 过滤或跨设备聚合。

**Validation Policy**:
摄入门卫（SegmentValidationPolicy / UsageValidationPolicy）：拒收未来时间戳、非法区间等畸形数据。拒收即丢弃，不修复——采集端负责数据正确性。
