# Observations 全链路实施交接

2026-09-11。模型判断已由用户确认；本文是实施交接，未代表代码修复或运行验证完成。
事实依据来自当前源码静态检查。实施前复核工作区，避免覆盖其他 Agent 的改动。
正式实施规格与当前验收状态见[Observations PRD](../../.scratch/observation-convergence/PRD.md)。
本文保留静态证据与实施定位，后续任务从该 PRD 拆分。

## 目标与权威规则

**让观测模型决定事实的身份、完整性与存储，交付机制负责保管和传递快照。**
用户要求本轮覆盖存储、Runtime 及所有相关代码；只增加服务端入口不算完成。
同 Fact 的 Observer、FOI、Kind、Aspect 固定，保留 Revision 表达正常快照更新。
完整规则以[模型基线](observations-model.md#事实与对象)为准。

新事实以生产者稳定 Id 和自身 Kind 成立；旧数据库行 Id 与完整旧写入键依照
[迁移映射](observation-storage-migration.md#2-事实身份保留行身份保留旧写入键)保全。
App 产品 FOI、目录纠错、历史未知、无关系事实、Measurement 暂缓及暂停的生产演练均沿用既有决定。

## 已核实的实现差异

| 位置 | 差异与影响 | 实施要求 |
| --- | --- | --- |
| `FactStore.IngestAsync` / `Apply` | 每条 Fact 必须提供 Stream/Subject，Kind 从 Stream 取得；完整观测无法独立保存 | 建立接收事实自身身份、Kind 与完整观测信息的统一写入核心 |
| `FactSnapshot` / `FactStore.Apply` | 上传 Fact 没有自身 Kind；按 Owner/Stream/FactId 查找，再生成另一个数据库 Id | 新原生契约使用生产者稳定 Id；既有事实经显式旧身份对应进入相同核心 |
| `FactRecord` / `AppDbContext.ConfigureFacts` | StreamId、旧 FactId 不可空，事实必须引用 Stream | 同步调整实体、数据库约束和消费者，使新事实不依赖旧交付键 |
| `ResolveObservation` / `FactAspects.IsValid` | 原生分支接受 null Collector/FOI，Aspect 也允许 null；HTTP 只检查协议版本 | 新原生输入严格完整；历史未知仅由明确的兼容契约承接 |
| `ResolveObservation` / `Apply` | Relations 是否为 null 决定新旧分流，核心按 Source/Payload 推断 Aspect | 旧输入在适配处转换；核心处理统一观测，不用字段遗漏识别兼容身份 |
| `FactStore.Apply` | 高 Revision 目前可替换 Collector、FOI、Aspect | 按已确认的同一事实不变量拒绝更换；实际观测变化生成新 Id。产品目录维护另循原规则 |
| Runtime 上传 / SDK / 第一方生产者 | 活跃链路仍组织旧 Stream 事实；新字段贯通未使新契约独立 | 逐个切换 System、Browser、VRChat、SDK、协议、Runtime、缓存和 HTTP 的实际生产路径 |
| Browser `protocol.ts::snapshotRevision` | 用 EndTime 毫秒值充当 Revision；结束时间缩短会使版本变小，同结束时间内容变化不能表达为更高版本 | 为变化快照维护稳定、可持久恢复的单调版本；迁移时保留已有身份及待发版本 |
| `FactHttpTests.Foi` | 使用 `SegmentBatch()` 旧 Stream 骨架，未证明新模型独立 | 新增无 Subject/Stream 的真实入口测试，并覆盖所有第一方完整链路 |

对应源码位于 `server/Heartbeat.Server/{Services,Entities,Data}`、
`shared/Heartbeat.Core/DTOs/Facts/FactUploadRequest.cs`、`shared/Heartbeat.Core/Facts/FactAspects.cs`、
`collection/hub/Heartbeat.Collection.Hub/Collectors/Runtime` 和
`collection/collectors/Heartbeat.Collector.Browser/src/protocol.ts`。
这张表是已核实的起点；实现时须盘点所有活跃读写及交付消费者，不能把表外路径视为已符合模型。

## 实施顺序与完成条件

1. **先复现契约缺口。** 最小原生观测只提供认证 Owner、生产者 Id、Kind、Collector、FOI、Aspect、
   Result、Time、Revision，Relations 为空；通过真实入口验证保存、重传、修订、读取，并先确认现状失败。
   同时为缺失必需观测信息及改变同 Fact 不变量建立最小失败测试。
2. **贯通统一核心和存储。** 新输入校验、旧身份转换各司其职，汇入同一写入核心及唯一 Facts。
   保持 Owner 隔离、修订比较与 Fact/Relations 原子性；旧导入先到、原生重放先到或旧缓存迟到，
   均保持既有行身份且只保管一条事实。新旧 UUID 碰撞显式拒绝，不能覆盖其他事实。
3. **切换所有活跃链路。** System、Browser、VRChat 经 SDK/Runtime/HTTP 真正提交新契约；
   迁移缓存、恢复进行中 Segment、精确 ACK、Gap 和协议升级继续保证保管。
   交付和管理如仍需要分组，由它们自身维护；新事实的业务身份、FOI、Kind 不由这些分组决定。
4. **迁移读取与分析。** 盘点原始读取、设备/App/账号/本人查询、报表、回放、Recap、Question、
   Dashboard 及相关投影。无旧 Stream 的新事实也能被适用消费者读取；未知 Aspect 保留原始结果，
   同 App 多设备事实只沿准确事实关系归属。产品维护保留平台身份依据。
5. **全链路验收。** 覆盖重复/乱序/同版本冲突、Segment 增长和缩短、固定身份属性、重启/离线/迟到 ACK、
   历史未知与原生必填、Owner 隔离、关系同步、旧缓存接管及产品纠错后的重放。
   先跑改动对应测试，再执行仓库规定检查；平台无法运行的验证记录可重复步骤和明确承接者。
6. **清理并收尾。** 删除已无消费者的旧主路径；每条保留兼容代码对应真实数据/安装版本、退出门槛和验证方式，
   依照[兼容台账](compatibility-debt.md)维护。同步领域词汇、架构文档和已有 issue/PRD，分别报告
   代码实现、自动验证及真实安装/生产验收状态。既有部署与资源演练暂停范围保持。

新入口内部伪造 Stream、让所有新事实永久经过旧 adapter、直接把历史 FactId 当作现有行 Id，
均不满足验收。历史迁移和有实际消费者的兼容资料按既有保全规则保留；“所有代码”要求覆盖实际依赖，
不等于机械删除每个旧名称。
