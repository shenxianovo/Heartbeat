# Observations 全链路实施交接

2026-09-11，按已整合的 Tickets 01–08 与当前源码刷新。本文区分当前实现和实施前诊断；
最终候选的全仓/组合验证与覆盖映射由 [Ticket 09](../../.scratch/observation-convergence/issues/09-contract-and-integrated-verification.md)记录。
正式规格和全轮状态见[Observations PRD](../../.scratch/observation-convergence/PRD.md)。
01/02/03/07/08 已完成；04/05/06 的实现、自动验收与双轴审查已整合，真实安装门禁仍使三票保持
`ready-for-human`。以下代码状态不表示业务库迁移、真实安装或全轮验收已完成。

## 目标与权威规则

**让观测模型决定事实的身份、完整性与存储，交付机制负责保管和传递快照。**
用户要求本轮覆盖存储、Runtime 及所有相关代码；只增加服务端入口不算完成。
同 Fact 的 Observer、FOI、Kind、Aspect 固定，保留 Revision 表达正常快照更新。
完整规则以[模型基线](observations-model.md#事实与对象)为准。

新事实以生产者稳定 Id 和自身 Kind 成立；旧数据库行 Id 与完整旧写入键依照
[迁移映射](observation-storage-migration.md#2-事实身份保留行身份保留旧写入键)保全。
App 产品 FOI、目录纠错、历史未知、无关系事实、Measurement 暂缓及暂停的生产演练均沿用既有决定。

## 当前实现与承接边界

| 组件 / 契约 | 已整合行为 | 实施证据 |
| --- | --- | --- |
| HTTP / Facts | `ObservationSnapshot` 自含 Id/Kind/Collector/FOI/Aspect/Result/Revision/家族时间，空关系有效。`IngestObservationsAsync` 验证完整原生输入；显式旧适配共用 `SaveSnapshot`，只存一份 Facts | [01](../../.scratch/observation-convergence/issues/01-independent-observation-custody.md)；`FactHttpTests.Independent*`、`IndependentObservationCustody` 追加迁移 |
| SDK / 协议 / Runtime | `Kind` 明确区分原生和历史。原生可无 Subject/Outputs/Binding；CollectorId 是 Observer，DeliveryInstanceId 仅表示保管者；持久重启与完整快照 ACK 不确认后来修订 | [02](../../.scratch/observation-convergence/issues/02-native-observation-delivery.md)；公开协议 transcript、真实 HTTP + PostgreSQL |
| 通用缓存 | SDK4 / Runtime9，`facts.observation:2`；支持 SDK1–3 / Runtime1–8 真实历史 DTO 矩阵。旧事实保持 Kind=null 和原键；转换失败保全，旧包拒绝新状态 | [03](../../.scratch/observation-convergence/issues/03-legacy-fact-and-cache-takeover.md)、[缓存台账](observation-cache-compatibility.md) |
| System | Package1.2.0；AppMonitor / InputEventBuffer → NDJSON ingress2 → 通用 SDK/InProcess。新 Fact 无 Binding；旧两 Stream 仅保管旧条目/真实 Gap；实时来源来自已 ACK 原生 Source | [04](../../.scratch/observation-convergence/issues/04-system-observation-cutover.md)、实际生产者 HTTP、旧 loader 保全脚本 |
| Browser | 持久扩展 UUID、journal5、单调 Revision；精确旧 keys 恢复、读取 fence、`facts.recover` 只读取得已 ACK 高水位。多窗口及完整浏览器重启保留独立事实 | [05](../../.scratch/observation-convergence/issues/05-browser-observation-cutover.md)、[专有缓存](../../collection/collectors/Heartbeat.Collector.Browser/cache-compatibility.md)、真实 ExternalHost/HTTP 与临时 Chrome 运行 |
| VRChat | 实际 ManagedProcess 新 Fact 是明确账号 FOI，关系为空；checkpoint4 先提交通用能力要求，启动/候选/LastKnownGood 共用保护；旧未知账号不被当前账号接管 | [06](../../.scratch/observation-convergence/issues/06-vrchat-observation-cutover.md)、托管进程/HTTP 与专有历史矩阵 |
| 对象 / 产品 / 本人 | 准确 Fact 关系用于多设备归属；used-by 独立维护。目录维护按精确 AppReferenceEvidence 保留原平台依据及 Fact Id/Revision/Result/Time，原快照可重放 | [07](../../.scratch/observation-convergence/issues/07-object-relations-and-products.md)、IndependentRelations/Products HTTP |
| 分析 / Dashboard | 无 Stream 的已知 Aspect 进入原有查询/报表/知识；未知 Source 如实空、未知 Result 完整读取。客户端任意 JSON 无损；Question v3、Recap 契约 hash 按需失效，不批量调用真实 LLM | [08](../../.scratch/observation-convergence/issues/08-analysis-and-dashboard.md)、IndependentAnalysis/Knowledge HTTP、前端 verify |

SDK/协议中保留的 `payload` 承载完整 Result，原始 HTTP 读取的 `payload` 是 `result` 的兼容别名，
不形成第二份内容存储。旧 Subject/Stream、Gap 和管理身份的存在本身不表示新事实仍依赖它们；
其真实消费者、版本与退出证据见[兼容债务台账](compatibility-debt.md)和[缓存接管](observation-cache-compatibility.md)。

## 仍待现场验收

- **04 / owner**：真实 Windows/macOS 安装、权限、原生窗口/标题/away/输入回调；按 [System README](../../collection/desktop/Heartbeat.Collector.System/README.md)逐平台执行。
- **05 / owner**：真实 Chrome/Edge 原 Profile 升级；按 [Browser 安装步骤](../../collection/collectors/Heartbeat.Collector.Browser/cache-compatibility.md#自动验证与真实安装步骤)备份、核对安装 UUID、升级/离线/重启与准确读回。临时 Chrome fixture 不替代这些 Profile。
- **06 / owner 与 Headless 发布维护者**：linux-x64 真实安装和 VRChat 账号授权/断线恢复；按 [VRChat README](../../collection/collectors/Heartbeat.Collector.VRChat/README.md)逐账号核对。
- **既有 observation-storage 发布门禁 / owner 与发布维护者**：完整安装 Package/content hash、缓存版本及待发/进行中/隔离清单，批准的最长离线与回退窗口。现场证据未齐前不能删除兼容。业务库迁移、部署与暂停的完整副本资源/恢复演练仍属该既有门禁。

上述工作不另建重复 backlog，不将 09 的代码收尾或自动通过当成三票现场验收。

## 历史起点：实施前静态诊断

以下表格记录 01–08 实施前发现的具体根因与当时实施要求；这些定位帮助解释改动原因，
当前处理结果以上表及各票证据为准。表内“仍/目前”等字样均指实施前状态。

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
这张表是当时核实的起点；09 仍须盘点所有活跃读写及交付消费者，不能仅以上述定位代替全库验收。

## 全轮完成条件（保留原实施顺序）

这些是持续适用的验收条件，不是宣称各项现场工作已通过。各票实际结果和 09 的最终组合证据分别记录。

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
