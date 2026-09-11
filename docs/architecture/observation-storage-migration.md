# 五表存储迁移映射与实施

状态：2026-09-11，已实现服务端存储与旧协议入口转换，隔离 PostgreSQL 自动验证通过。
目标为用户确认的[五表方案](observation-storage-minimal.md)及 [ADR-059](../adr/059-observation-storage-five-tables.md)。
实际业务库尚未迁移；完整副本资源/恢复演练仍暂停，未执行生产操作。

## 先确定迁移边界

- 新目标为 Collectors、Objects、Facts、Relations、RelationMembers 五张领域核心表。
- 五表不是整个 Heartbeat 数据库仅剩五表。用户、认证、运行管理、App 目录、图标、知识与派生缓存有自己的职责。
- Facts 最终只有一份完整 Result；允许停写期间的搬迁工作空间，不允许切换后两套完整事实长期双写。
- 原事实数量、结果内容、Revision、家族时间与独立身份必须保住；关系增加不合并 Facts。
- 不修改已发布 EF migrations；不恢复上一轮用户暂停的资源演练，不在本任务部署生产。

## 已核对的实际起点

代码已有五批 Observer/Target 改造，但这不证明生产库已完成这些迁移。
[上一轮脱敏报告](../../.scratch/observation-identity-targets/cutover-report.json)记录的 2026-09-11 副本
最后迁移为 `20260908141403_NativeFactCustody`，包含 223,313 Segments 与 1,939,969 Events。
这是旧记录，不是本次重新查询的当前数量；该轮全量对照与资源恢复尚未完成。

追加迁移至少覆盖两个入口：

1. 只有 NativeFactCustody 的已部署家族基线；按既有 EF 顺序先执行尚未应用的历史迁移。
2. 已有 Observer/Target、应用上下文、账号、本人关联与最终设备回填的最新代码基线。

不能跳过中间 migration 却伪造其已执行；资源评估应包含完整待执行链的回填成本。
如需另设计绕开中间搬迁的升级入口，应单独比较并记录决定，不擅自改写已发布迁移。

## 1. 对象与 Collector 映射

| 当前依据 | 新位置与建议规则 | 不允许的推断 |
| --- | --- | --- |
| Devices 的 OwnerId、HardwareId、DeviceName | machine Object；Scope 使用固定设备命名空间，Key 沿用稳定 HardwareId，Name 取 DeviceName | 不按设备名合并，不把 `subject:account/person:...` 旧导入传输标记当真实机器 |
| Apps 的 Id、Key、DisplayName | 一个全局产品对应一个 app Object；Scope 使用产品目录命名空间，Key 沿用产品 Key；既有 Apps.Id 到 Object.Id 建稳定对应 | 不把 win/mac 平台身份当两个产品，不按显示名猜产品 |
| AppIdentities | 保留平台身份到产品的现行目录映射及其纠错能力；不将每个平台身份另建为 app Object | 不丢弃某条事实原有的平台应用证据 |
| ServiceAccounts 的 ServiceKey、ServiceAccountId | account Object；Scope 表达真实服务，Key 为实际服务账号 ID | 不用昵称、CollectorId 或当前登录账号补历史 |
| ServiceAccounts 仅有 LegacySubjectId | account Object 沿用实际 ServiceKey 与原 SubjectId；Name 保留 null，不新增新旧命名空间 | 不与某个当前真实账号自动合并 |
| Persons.Reference | person Object；Key 为现有独立本人引用，Owner 保留 | Owner 身份不替代 Person，也不自动生成全部历史的使用者关系 |
| ApplicationContexts | 提供原 Device、App 及对应事实的迁移依据；不生成 application-context Object | 上下文存在不证明安装或使用的完整历史时段 |
| 非空、可靠的 Fact.ObserverId | 对应具体 Collector；同一 Owner 下同一观测者只建一行 | 不把 Target 相同的 Collector 合并 |
| Browser 的持久扩展安装 UUID | 使用已经保存的 ObserverId 或旧流中明确的 externalHostIdentity | 不改用可承载多个安装的 Runtime CollectorInstanceId |
| System/VRChat 的可靠 native CollectorInstanceId | 在既有回填规则明确适用时恢复具体 Collector | legacy-import 的实例缺失不由当前安装补造 |
| 没有可靠 Observer 或 FOI | Facts 对应引用建议允许历史 null，原依据继续保留 | 不创建一个假的“真实 Collector”或强行猜 App/机器 |

对象和 Collector 映射在一次迁移及重试间必须稳定：为旧资料保留 ObjectId/Collector 对应，
可通过原资料上的唯一外键列承载；不能每次回填重新生成 UUID。已有 UUID 只有在所有权与身份
无冲突时才可复用，不能假定安装 UUID 在不同 Owner 下天然唯一。
Collector.Kind 优先保留实际来源词汇，如 `vrchat.account`；同一观测者若对应多种旧 Source，
Kind 保留 null，逐条 Source 继续保存，不能任选一种导致另一类来源消失。

### Owner 与产品目录

Collectors、Facts、Relations 保留 OwnerId。Objects 的机器、账号、个人是 Owner 私有，
App 产品继续全局共享：建议 Objects.OwnerId 可空且仅 app 允许全局作用域。
私有对象按 `(OwnerId, Kind, Scope, Key)` 唯一，全局 app 按 `(Kind, Scope, Key)` 唯一，
使用明确的条件唯一约束，不依赖 nullable 普通唯一键碰巧达到隔离目的。

引用必须限定为本 Owner 的私有对象或全局 app；Collector 必须属于 Fact 的 Owner。
RelationMembers 的可访问范围由所属 Relation 的 Owner 决定。跨表所有权与对象种类需要
数据库检查和事务写入共同保护；普通 `ObjectId` 外键本身不保证这些条件。
全局 App 展示不携带任何 Owner 的设备、账号、关系或事实资料。

上述 Owner 字段和历史 null 已进入两项追加迁移。
新原生输入仍必须给出有效 Collector/FOI；历史未知不能成为新生产者绕过校验的常规入口。

## 2. 事实身份：保留行身份，保留旧写入键

当前每个家族独立以 Id 为数据库主键，以 `(OwnerId, StreamId, FactId)` 标识同一上传事实。
原生写入为新行生成另一个 UUID；数据库 Id 通常不等于客户端 FactId。
更早导入可能通过 `ProjectedSegmentId(StreamId, FactId)` 或输入 FactId 被原生快照接管。
依据：[FactStore.Apply/FindLegacy](../../server/Heartbeat.Server/Services/FactStore.cs)、
[旧导入规则](../../server/Heartbeat.Server/Services/FactStore.LegacyImport.cs)。

实施规则：

1. 迁移前检查 Segments.Id 与 Events.Id 是否相交。没有冲突时，新 Facts.Id 保留原家族行 Id。
2. 若存在相同 UUID 对应两个独立家族事实，保留两条；盘点报告列出完整旧键，生成持久的显式改号
   对应并迁移所有引用。未完成该对应前停止切换，不能用 ON CONFLICT 去重或随机改号后丢掉映射。
3. 切换阶段在 Facts 保留可空的旧 StreamId、FactId，以及 Kind、OwnerId，
   对非空旧写入键建立 `(OwnerId, Kind, StreamId, FactId)` 唯一约束。新 Id 不取代旧幂等入口。
4. 旧原生快照按完整旧键找到新 Facts；历史导入接管继续用完整确定性规则与 Owner/来源检查，
   不使用标题、时间相近、同 FOI 或同 Result 判等。接管保持新 Facts.Id。
5. 新 Collector 对全新事实可直接生成目标 Id；切换时仍进行中或待发的旧事实继续走旧身份转换，
   直到正常终结/确认。不能在进行中把原客户端 FactId 当作已经存在的数据库行 Id。
6. 旧、新表示收敛不增加 Revision。同版本比较转换后的完整语义；低版本不覆盖高版本，
   高版本继续遵守现有起点/事件时刻固定规则。同 Revision 改 Collector/FOI/结果/时间或绑定关系应冲突。

这意味着五表图中的“生产者生成 Id”适用于新原生事实，存量迁移允许保留旧服务端行 Id。
旧写入键属于兼容元数据，不是新业务归属；不能仅因新表已有 Id 就立即删除它们。

## 3. 家族字段与结果映射

| 旧字段 | 新字段/保留位置 |
| --- | --- |
| 所属 Segments / Events 表 | Facts.Kind = segment / event |
| Id | Facts.Id，按上面的冲突盘点规则 |
| OwnerId | Facts.OwnerId，原值 |
| ObserverId | 映射后的 CollectorId，历史未知为 null |
| TargetKind/TargetId | 作为逐业务解释 FOI/关系的输入，不直接改列名为 FoiId；转换及未知情况的原依据未有等价去处前保留旧引用 |
| StartTime / EndTime | Segment 的 StartTime / EndTime，原值 |
| Timestamp | Event 的 StartTime；EndTime = null |
| Revision | 原值，不因迁移升级 |
| Payload | Result 原 JSON 值完整保留，不添加新外壳，不裁去未知成员 |
| Source | 切换阶段保留逐条来源证据，不能因未知 Collector 而丢失 |
| AppIdentityId | 保留原平台应用证据和有效外键，供产品合并、拆分和重放解释 |
| StreamId / FactId | 上节旧写入键；Stream/Subject 原定义继续服务旧导入与 Gap |

Result 是语义改名，不要求趁迁移重整 JSON。已存 `activityKey` 保持；更老缓存的 `identityKey`
仍通过现行确定性规范化转换。事实时间使用数据库现有微秒精度，Gap 保留既有 tick 精度。
零长度 Segment 是现有合法输入，不得在迁移时用 `EndTime > StartTime` 约束静默排除。

### FOI 与 Aspect：逐业务转换

| 旧事实及可靠依据 | FOI | Aspect / 处理 |
| --- | --- | --- |
| System 桌面活动，有设备依据 | machine | `desktop-activity`；保留桌面活动原义，包括没有 App 的状态 |
| System 已识别输入 Event，有设备依据 | machine | `input`；完整保留 eventType/codeSet/code，不强行归到某 App |
| Browser 活动，有明确产品及设备依据 | app | `selected-page`；原窗口细节仍在 Result，每个窗口的 Fact 保持独立 |
| Browser 活动仅有设备依据 | machine | `selected-page`，保留 Browser 已有活动语义及历史设备粒度，不补造 Chrome 或窗口身份 |
| VRChat 账号位置活动 | account（包括有旧身份依据的未知历史账号） | `account-location`，世界/实例细节原样保留 |
| 个人 Target 或其他自定义事实 | 有明确直接对象依据时引用；否则 null | 已有 activityKey 的 Segment 使用 `activity`；未能解释的其他结果 Aspect 保留 null，Result 不丢失 |

Aspect 由既有活动/输入形状确定，不建立新旧分类体系。只有 Source、实际 Payload 形状与原发布契约
共同支持时才采用具体含义；例如 Source=system 的任意 JSON Event 不能全部宣称是输入事件。
应用上下文上的自定义 Event 也不能因关联相同就被解释成选中页面。
对所有已知/未知分支建立映射清单后，才能声明覆盖了全部历史数据。

## 4. 关系：保留证据及事实绑定

| 旧依据 | 建议关系 | 有效时间与限制 |
| --- | --- | --- |
| PersonAssociations | `used-by`；device 或 account，加 person 角色 | 原 Start/End，包括明确的无界含义；每项原确认独立保留，不改写原 Fact |
| Browser Fact 的设备与产品依据 | `observed-on`；device、app | 仅该 Fact 的有效时间，表示此次应用观测发生的设备，不升级为安装或注意力使用证明 |
| System 桌面活动的明确设备/App 证据 | `observed-on`；device、app | 保留该次活动中的产品关联，不把没有 App 的桌面状态补成产品 |
| ApplicationContexts 孤立资料 | 不自动生成运行或安装关系 | 无时段依据，不将空边界填成无限期 |
| ServiceAccounts/ServiceProducts | 保留服务身份与产品目录映射 | 不据此建立 Windows/Quest 运行关系；服务归属与本机应用使用分别解释 |
| VRChat account Fact，无设备证据 | 不生成设备/app/account 使用关系 | Collector 宿主、昵称、当前登录或同时间 System 段都不足以证明关联 |

原生关系/明确人工确认以后才能表达 `installed-on`、`application-account-use` 等更强含义；
不要求本次历史数据能填满所有目标关系种类。

**事实所属设备必须能精确追溯，不能只按 App 与时间关联。**
同一时间 Mac 与 Windows 都产生 Chrome Facts 时，若只将两组 app/device 关系与事实按时间相连，
Mac 查询会混入 Windows 的事实。建议利用已确认的 Evidence 字段保存该关系所支持的准确 FactId：

```json
{"factId":"<Facts.Id>"}
```

事实专属设备关联使用该引用绑定原 Fact，不通过时间重叠猜配。
对人工使用者关联才按已确认对象与适用范围筛选。Evidence 里的 Fact 引用需要 Owner/存在性校验；
JSON 本身不提供外键，应在事务入口及数据库保护中落实，不把它当无约束备注。
每种关系的角色、对象 Kind 和基数固定；例如 observed-on 必须恰有一个 device、一个 app。

从 Fact 生成的关系随其 Revision 在同一事务中更新：更高版本缩短区间，关系也随之缩短；
重试不得反复插入新关系。关系身份按“同一 Fact + 关系用途”确定或持久映射，不能按参与对象归并
来自不同 Facts 的证据。人工关系有独立身份与依据，不随 Collector 更新而被覆盖。
数据库由 Evidence 生成 FactId/AssociationId 索引列并建立 Owner 复合外键；不是第二份权威证据。
成员种类、Owner 与基数在提交前验证，禁止移动成员归属，成员写入更新关系行以串行化并发修改。

## 5. 既有资料与查询消费者

- Devices 的 LastSeen/CurrentAppIdentity、账号管理授权、Collector 安装配置不搬成 Result。
  原资料可增加稳定对象引用作为配套资料；未盘点消费者前不删除原表。
- Apps/AppIdentities/目录覆盖/图标与合并继续保持全局产品语义。
  产品纠错必须沿原 AppIdentity 证据更新对应 Object/Foi/Relation 引用并保持 Fact 身份、Revision、Result、时间；
  部分平台身份拆分不能移动整个产品的全部 Facts。保留该证据是必须项，不是可任意清理的 legacy 字段。
- 原始 Fact、活动、输入、设备/App/账号/本人、Experience/Recap/Question 的查询改读唯一 Facts。
  可以建立 SQL 投影，不再复制完整活动/输入 Payload。
- 对外旧行 Id、DTO 中 StreamId/FactId、产品数字 Id 等引用要列消费者清单；不能只改 EF 实体。
  已有知识确认与人工数据原样保留；受语义变化影响的派生缓存通过版本失效处理，不擅自批量调用 LLM。
- 本次不重定时长计算口径；迁移对照区分身份/内容恒等与已明确的对象表达变化。

## 6. 离线、重放与退出

| 保留路径 | 实际消费者 | 移除条件与验证 |
| --- | --- | --- |
| Facts 旧写入键、旧 FactStore/LegacyImport 转换 | 旧 `/facts`、segments/input 导入及尚在缓存中的旧身份事实 | 全部旧安装升级、进行中事实终结、缓存与备份恢复窗口退出；证明双向到达顺序只保管一个事实，旧 API 引用完成迁移 |
| Streams / Subjects / FactGaps | Gap、ACK、旧身份接管、运行管理 | 业务归属不用它们；交付/管理仍有消费者就保留，删除必须另有同等保管路径，不以五表数量为清理理由 |
| Runtime JSON 读取器与完整快照 ACK | 当前 v6、仍支持的 v1–v5 历史缓存 | 保留备份与 Delivered、原 FactId/Revision/时间/Gap；验证转换后新旧 ACK 都只确认准确的已发送内容 |
| Browser pendingSegments/foldState/安装身份 | 多窗口、Service Worker 重启、离线待发与初始化绑定 | 原窗口 Fact 不按 App 合并；有设备依据才绑定，旧队列排空且升级/重放已验证后退出旧 reader |
| VRChat checkpoint v1/v2/v3 | active、pending Facts/Gaps，含旧未知账号 | 保留旧事实终结与新账号分离；不以当前账号补旧快照；真实安装升级与恢复窗口结束后才退出 |
| Source/AppIdentityId 与资料映射 | 未知历史解释、App 产品纠错、查询与重放 | 属于仍需保存的证据；迁往有同等表达力的固定位置并完成消费者切换前不能删除 |

当前 HTTP 接受旧形状，Runtime ACK 比较 Observer/Target/时间/Payload 等完整字段。
建议先在 Analytics 兼容入口转换到新存储，旧 outbox 保持原已发送请求及确认规则；
随后为新原生发布增加明确的协议版本/形状，不能让未知字段被旧端默默丢掉。
同批 Facts 与派生关系原子提交，成功后才可 ACK；失败保持原请求可重试。

## 7. 物理搬迁与验收顺序

1. **只读盘点**：实际 migration 历史、两家族行数/Id 相交、Owner 与引用完整性、Observer 缺失及
   类型冲突、Target/Source/Payload 分布、旧缓存版本与所有原 Id 消费者。
2. **实现转换并验证**：先覆盖下表最小行为，再补齐约束与新旧 API 查询；新表只能有一条事实写入路径。
3. **隔离副本演练**：按 ADR-058 的同一候选镜像跑完整升级、核对、失败重试与恢复；当前暂停状态需另行恢复，
   本次不执行。不能拿空库测试代替已有约 216 万条事实的副本证据。
4. **推荐物理起点**：评估原地将 Segments 演进为 Facts，再搬入 Events，避免先复制两份完整历史。
   旧输入表的数据/索引搬迁、FK、WAL 与工作空间仍有成本；需实测后决定分批、索引创建时机和检查点。
   若采用可重试分批迁移，进度标记与每批搬迁原子提交；若单事务，则失败整体回滚，不能混用两种恢复承诺。
5. **发布时停写与备份**：迁移全部验证通过才启动新 Analytics，首次失败前备份不得被重试覆盖。
   不把“EF 已无 pending”当作数据回填已完成；新完成标记须被 `--check-database` 与生产启动核对。
6. **切换后保管与恢复**：若新服务已接收并 ACK 新事实，回滚前必须保管这些新增事实及必要关系/身份资料，
   不能假定 Collector 仍留着已确认数据。旧版不能解释的新模型数据须隔离保存，供修复后重放；
   备份恢复不等于新模型可无损 Down。

512 MiB 恢复 OOM、768 MiB 仅恢复成功的上一轮记录不能证明本次完整 1C1G 整机可用。
磁盘、WAL、数据库/Analytics/系统总内存及真实停写耗时必须实测，不先承诺资源与耗时。

## 8. 实施必须通过的行为验证

| 主题 | 必须证明 |
| --- | --- |
| 身份与合表 | 两流同 FactId 不合并；两家族同 Id 的预检/改号映射不丢行；两 Owner 不互串 |
| 最新快照 | 重复、乱序、同版本冲突、合法结束时间缩短保持；未知 JSON 成员原样保管 |
| 旧导入接管 | 历史先到/原生先到/旧缓存晚到均只一条事实，不重长已纠正区间 |
| FOI 与未知 | System 无 App、Browser 仅设备、未知账号、未知 Observer、自定义事件均有明确映射且可读取 |
| 设备绑定 | Mac/Windows 同时产生同 App Facts，按设备查询不交叉；关系跟随 Fact 修订，不制造虚假安装时段 |
| 关系/Owner | 成员种类与基数、证据引用、跨 Owner、重复生成、悬空与并发删除均受保护 |
| 产品维护 | App 合并、部分平台身份纠错、删除覆盖后的回退都保住身份/结果，准确更新相关引用 |
| 缓存链路 | System、Browser、VRChat 经 Runtime → HTTP → PostgreSQL → 查询，含断线、重启、迟到 ACK 与 Gap |
| 逐行对照 | 映射后新 Facts 与两旧家族一一对应；Result JSON 值、Revision、时间、原平台证据逐行一致，所有关系有依据 |
| 发布恢复 | 完整待执行迁移链、失败不启动、重试幂等、升级前备份恢复、已 ACK 新事实保管、资源预算 |

扩展现有 [FactStoreTests](../../server/Heartbeat.Server.Tests/Services/FactStoreTests.cs)、
[FactHttpTests](../../server/Heartbeat.Server.Tests/Services/FactHttpTests.cs)、
[家族迁移 fixture](../../server/Heartbeat.Server.Tests/Services/FactFamilyMigrationTests.cs)及 Browser/账号/本人迁移测试。
历史迁移测试保留其原目标，新测试验证从历史目标到五表的追加升级，不改旧断言冒充新升级已通过。

## 本次实施与保留边界

追加 `20260911024930_ObservationFacts` 原地将 Segments 改为 Facts，搬入 Events 后删除 Events；
`20260911025354_ObservationObjects` 建立对象、Collector、关系与数据库约束。旧迁移未改写。
两家族 Id 相撞会在任何搬迁前明确报错并回滚；不静默合并、不临时随机改号。
本次采用 EF 单事务迁移；回填在安装持续写入触发器前批量完成，重试依据 migration history。

Objects 的稳定 UUID 保存在现有资料的唯一 ObjectId 外键中。事实只保存一份 Result。
System 使用 machine FOI，Browser 有明确应用上下文时使用 app FOI，VRChat 使用 account FOI；
已有 AppIdentity 证据继续支持产品筛选和纠错，设备未知时仍为空。

现有 `/facts`、导入、管理入口继续接受原形状；数据库在同一事务中转换为 FOI/Aspect/关系，
现有查询从 Facts、Objects 与精确绑定的 Relations 读取。DTO 新增 FoiId/Aspect，原字段保持可读。
`Segments`/`Events` 是 EF 对同一 Facts 表的 Kind 投影，不是双写的物理事实表。
本轮支持现有 Segment/Event 生产者；Measurement 的业务输入、独立原生 FOI 发布协议及对象管理 API
尚无已确认采集需求，不提前实现。新入口落地时替换转换入口，不能让其在当前触发器下被旧字段覆盖。

Source、AppIdentity、Stream/FactId、原 Target 与辅助资料保留各自现有消费者；退出条件见第 6 节。
没有新增 legacy-* 类型、作用域或迁移 Gap，也没有改变时长计算口径。

验证及剩余门禁见[实施 PRD](../../.scratch/observation-storage/PRD.md)。
演练脚本已适配五表候选，保留从 NativeFactCustody 完整备份升级与原版本恢复的流程；
仅验证了脚本和隔离测试 SQL，尚未恢复完整副本演练。真实资源与生产验收不能由自动测试替代。
