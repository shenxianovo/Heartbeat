# 本人关联维护与事实查询

[Ticket 04](../../.scratch/observation-identity-targets/issues/04-person-associations.md) 复用
[System](system-observation-targets.md)、[Browser](browser-observation-targets.md) 和
[VRChat](vrchat-account-observation.md) 的 Observer/Target、业务资料和事实家族。
本批不引入 DataSource、统一对象表、通用关系图或 Measurement 采集。

## 本人身份与存储

认证 `User` 的 Id 是认证平台的 sub，负责访问及看板可见性；`OwnerId` 是数据隔离边界。
`Person` 表示被 Facts 描述的本人，在现有设置中明确建立，每个 Owner 最多一个。
它有独立的 bigint Id 和稳定 UUID Reference；UUID 不是 User.Id、设备身份、Collector 身份或账号 ID。
建立本人不建立任何使用者关联，也不解释旧 Person Subject 的历史。旧未明确信息保持原样。

追加 migration `20260910142956_PersonAssociations`，不修改已部署 migrations。

| 表 | 列及约束 |
| --- | --- |
| Persons | Id bigint identity PK；OwnerId → Users.Id Restrict；Reference uuid；OwnerId、Reference 分别唯一；(OwnerId, Id) alternate key |
| PersonAssociations | Id bigint identity PK；OwnerId、PersonId；DeviceId bigint nullable；AccountId bigint nullable；Start/End timestamptz nullable |
| 关联引用 | (OwnerId, PersonId) → Persons；(OwnerId, DeviceId) → Devices；(OwnerId, AccountId) → ServiceAccounts；均为 Restrict，不能跨 Owner |
| 关联目标 | DeviceId 与 AccountId 恰有一个非 null；不是可扩展的关系种类/对象 ID 列 |
| 关联区间 | null 表示开放边界；非 null 必须有限；双边有限时 Start < End |

ServiceAccounts 仅增加 (OwnerId, Id) alternate key 用于复合外键；账号身份、未知历史及 ServiceProducts
沿用 03。关联不会改变这些业务资料。应用上下文不直接建立使用者关联，沿已有 DeviceId 参与。
Person 的 Id、OwnerId、Reference 不可原地改变。删除被 Segment/Event 引用的 Person 被拒绝。
既有 `heartbeat_check_fact_target` / `heartbeat_restrict_target_change` 扩展 person 分支，保留 device、
application-context、account 的既有检查。个人分支对 Person.Reference 做同值 UPDATE，
产生 MVCC 行版本并持有更新锁，使旧 Repeatable Read 快照的并发删除得到 serialization failure；
只加行锁不能令旧快照看见后来提交的 Fact。同一本人的引用写入会串行等待，字段值保持不变。
关联存在时，原生外键也阻止删除或转移其 Person、设备及账号。

本批没有存量事实回填，没有自动创建 Person，没有自动关联任何 Owner 的设备/账号。
Segment/Event 表、行 Id、StreamId、FactId、Revision、Observer、唯一 Target、时间及 Payload 均保持。
已有索引继续服务家族时间及 Target 查询；关系新增 (OwnerId, DeviceId, Start, End)、
(OwnerId, AccountId, Start, End) 和 (OwnerId, PersonId) 索引。

## 维护与摄入入口

全部本人维护和本人查询位于认证的 `/api/v1/me/person`，Owner 只从当前认证身份取得。
不接受 body 内 OwnerId、PersonId 或用户名；额外请求字段被拒绝，不提供跨 Owner 的本人查询入口。
已有 public Dashboard 可见性规则不变，本人关系不会随 public 开关公开。

端点、参数与响应结构以 [PersonController](../../server/Heartbeat.Server/Controllers/PersonController.cs)
和 Development OpenAPI 为准。设置读取本人、设备/账号选择项及已确认关系，不按在线状态筛选；
明确建立本人操作是幂等的，要求已有 User 供给。创建、纠正及移除关系均在认证 Owner 内执行。
本人查询按事实家族、时间窗与页范围返回结果。

两个时间边界必须显式提交，允许 null；
HTTP 时间接受明确偏移并转为 UTC，拒绝非有限端点及亚微秒精度，避免 PostgreSQL 截断后改变范围。
不存在/其他 Owner 的目标或关联为 404，格式/范围错误为 400；数据库外键承担最终引用完整性。

原生个人事实示例：`{"kind":"person","reference":"11111111-1111-4111-8111-111111111111"}`。
Reference 必须是已明确建立的本人 UUID，在认证 Owner 内解析；未知、其他 Owner、空 UUID 和非法形状
被拒绝，不能通过摄入隐式建立本人。运行时与 Analytics 共享 `PersonReference` 的解析契约。
目标可以离线保存在 Collector/Runtime 的既有完整快照中；不要求每次观测在线登记。
个人 Segment/Event 继续经 Runtime 保管、重启、HTTP 摄入和同 Revision 重放收敛到同一家族行。
认证 User、旧 Stream Subject、采集宿主及 Observer 都不会补出一个个人 Target。

## 时间、去重与分页

关联使用 `[Start, End)`，空起点/终点分别为无下界/无上界；两边均空需要明确确认全部历史。
允许重复、重叠、相邻及不连续关联；多条记录保持可单独纠正/删除，存储时不自动压缩关系。

- Segment 必须与关联及查询窗口有正长度交集。返回的 `fact.start/end` 是原始边界；
  `effectiveIntervals` 是关联与查询窗口的交集并集，按起点排序。重叠/相邻合并，空隙保留。
  `effectiveSeconds` 仅为该条事实的有效覆盖秒数；零长度 Segment 没有有效覆盖，不进入本人 Segment 结果。
- 直接个人 Target 的 Segment 与查询窗口相交，无需关系；Event 按发生时刻判定。
- Event 在关联/查询起点命中，在终点不命中；返回原始 `occurredAt`，有效区间为空、秒数为 null。
- 不按 Target、Observer 或裸 FactId 合并事实。每个原家族行仍独立；Owner/Stream/FactId 身份规则不变。

数据库用相关 `EXISTS` 筛选关系，没有关系 join 扩大结果。Count、各 Source 事实数与分页都先基于
匹配的家族行计算，再按原始 Start/Timestamp 降序、行 Id 升序稳定分页；不会先 Take 再筛关系。
只加载当前页涉及设备/账号的关系计算区间并集；名称从当前 Owner 的具体资料解析，产品引用沿用 02/03。
单次请求使用 Repeatable Read，页、计数、资料和有效覆盖来自同一数据库快照。多次翻页是新的查询；
期间补录/纠正事实或关系后应重新筛选，不承诺跨多次请求冻结旧数据集。

统计仅返回该家族的各 Source 事实数，不提供跨来源覆盖时长之和。Person 页面显示逐条有效秒数；
Browser、VRChat 与 System 时长不能简单相加解释为注意力，既有 System Report 边界不变。

## 最小管理交互与验证

入口为设置 → 本人关联与事实（`/settings/person`）。选择已有设备或账号、明确时间后创建；
原关联旁可纠正、移除，成功后刷新本人事实。页面可选 Segment/Event、时间窗及翻页；
每条 Segment 展示原始区间与所有有效覆盖区间，设备上的 App 使用具体产品名称解释。
日期按浏览器时区输入；未编辑的 ISO 边界原样送回，保留数据库微秒精度。
Event 详情只显示身份、归属及发生时刻，不把原始按键序列展示到页面；公开事实 API 及持久化不丢 Payload。

真实 UI smoke：

```sh
dotnet build server/Heartbeat.Server --no-restore
npm --prefix frontend run build
node scripts/smoke-person-associations.mjs
```

要求 macOS、Google Chrome、Docker（PostgreSQL 18）、Node 与 .NET SDK，以及已有 `.env.local`
Auth 设置和 `.local/desktop/config.json` 开发 API Key。脚本只借用该凭据换取认证；数据全部写入
新建隔离 PostgreSQL，启动当前 Analytics 和构建后的真实 Dashboard。既有四类事实 fixture 先摄入，
随后在 Chrome 通过真实表单创建/纠正/移除关系并筛选本人事实，对照全部原始 Facts 未变。
不启动真实 Collector，不写日常数据库，不部署生产；临时浏览器 Profile、进程和容器退出时清理。
成功退出 0，报告和截图在 `.local/observation-person-04/`；脱敏报告随 Ticket 保存。

自动验证以 `FactHttpTests.Person` 为公开业务入口，补充 Runtime 重启/HTTP 重放和真实 PostgreSQL
migration/完整性测试。不是关系表 CRUD 的替代验收。完整结果、两轴 review 与已有 macOS 波动见 Ticket 04。

迁移发布遵循 [ADR-058](../adr/058-ci-database-migration.md) 和
[迁移 Runbook](../runbooks/analytics-database-migration.md)：候选镜像停写备份后 `--migrate`，再检查启动。
Down 明确拒绝有损降级，恢复依赖升级前备份。本批不新增兼容适配器或旧 Stream→Subject 业务查询。
01 的真实 System/Windows、03 的真实 VRChat 账号验收以及 05 的生产副本演练/切换门禁继续保留。
