# 观测身份与事实归属改造

Status: ready-for-agent

## Project Background

Heartbeat 仍在持续演进。用户在 2026-09-10 说明，项目已达到约 100k 有效代码行的规模
（用户提供的规模背景，非本任务重新统计）；观测范围已从单机应用扩展到跨设备活动、虚拟世界
和账号实体，原有 Machine-centric 活动模型已不足以承载这些业务。

Observations 是本轮演进的核心模型，存储是用户特别重视的部分。实施时应说明观测语义如何
映射到事实家族、身份、长期归属和业务资料，区分已落地能力、迁移期兼容与后续工作；不能只
通过字段改名宣称模型改造完成，也不能为了接入新对象继续把账号或虚拟世界活动解释成机器活动。
保护已有事实及离线待发快照，与支持新观测对象同样重要。

## Problem Statement

Observations 已在 System、Browser 的运行模型中得到验证，但事实上传、保管和查询仍经 Stream → Subject 解释归属。窗口、应用上下文、账号和本人因此无法按各自的业务含义自然连接；生产者的身份也与传输信息混在一起。

用户希望按设备或账号回看事实，以及通过明确的使用者关联汇集本人的事实。临时窗口只需帮助采集器维护并行活动，不应被迫登记为长期实体。改变业务归属时必须保住已经存储的事实和仍在客户端等待上传的快照。

## Solution

Observer 观察 FOI，产生 Facts；每条 Fact 直接关联具体 Observer 和唯一的 Target。System 的 Target 是设备，Browser 的 Target 是设备上的 App 应用上下文，VRChat 的 Target 是账号，直接描述本人的事实可以以本人为 Target。

应用上下文由设备与 App 产品共同辨认，跨启动保持，同设备同 App 的不同 Profile 共用一个上下文。窗口仍是 Browser 的直接 FOI，窗口细节随事实保留，关闭窗口不会删除历史事实。

Heartbeat 独立维护使用者关联及其适用范围；补充关联后，本人查询能够纳入已有事实。基础模型已经收口，DataSource 不进入本规格。

## User Stories

1. As an owner, I want facts to identify their concrete Observer, so that I can tell which collector produced them.
2. As an owner, I want an Observer to survive collector restarts and updates, so that its historical identity remains stable.
3. As a collector author, I want one Observer to observe multiple FOIs, so that parallel observations do not require artificial collector instances.
4. As an owner, I want different Observers to observe the same object independently, so that their evidence is not silently merged.
5. As an owner, I want every Fact to have one Target, so that its long-term attribution is unambiguous.
6. As an owner, I want System activity Segments to target their device, so that device history remains queryable.
7. As an owner, I want System input Events to target their device, so that keyboard and mouse summaries remain correct.
8. As a Browser collector author, I want windows to remain runtime FOIs, so that parallel page activity stays independently tracked.
9. As an owner, I want Browser facts to target the device's App context, so that application activity has a stable home.
10. As an owner, I want browser restart and Profile differences to preserve the device-plus-App context, so that they do not fragment application history.
11. As an owner, I want closed windows' facts to remain available, so that transient object lifetime does not determine retention.
12. As an owner, I want browser-selected pages to remain distinct from OS foreground activity, so that overlapping windows do not inflate attention statistics.
13. As an owner, I want a VRChat account to retain its own identity, so that the collector host is not mistaken for the observed object.
14. As an owner, I want a VRChat account to be identified by its service and observed service identifier, so that names and collector installations do not define account identity.
15. As a collector author, I want runtime observations to become facts through a small publishing interface, so that transport bookkeeping does not spread into business rules.
16. As an owner, I want pending snapshots to survive the upgrade, so that offline collection is not lost.
17. As an owner, I want replayed snapshots to update the existing fact, so that upgrading does not duplicate history.
18. As an owner, I want higher revisions and same-revision conflicts to retain their existing meaning, so that attribution changes do not weaken fact correctness.
19. As an owner, I want existing Payload and family time values preserved, so that historical information is not rewritten to fit the new terminology.
20. As an owner, I want unknown historical identities to remain explicitly unknown, so that migration does not invent evidence.
21. As an owner, I want account and device usage associations to be maintained outside Facts, so that I can correct attribution without rewriting raw observations.
22. As an owner, I want an association to apply to its confirmed time range, so that adding it today can correctly explain earlier activity.
23. As an owner, I want my view to include directly personal facts and facts from explicitly associated devices or accounts, so that I can review relevant activity together.
24. As an owner, I want overlapping association matches to return a fact only once, so that relationship joins do not duplicate results.
25. As an owner, I want device, application and account history to remain accessible after a collector goes offline, so that current connection state does not filter history.
26. As an owner, I want App catalog corrections to preserve application context references and fact identity, so that product maintenance does not orphan records.
27. As an operator, I want an additive, rehearsed migration and a bounded cutover, so that the deployed family tables and pending clients can be upgraded safely.
28. As a maintainer, I want compatibility code to name its consumers and exit conditions, so that the old attribution path can actually be removed.
29. As a maintainer, I want the final read path to use Observer and Target directly, so that Stream remains an internal delivery or identity concern.
30. As an owner, I want authorization and ownership isolation to remain intact, so that new target references cannot expose another owner's records.

## Implementation Decisions

### 已确认的业务基线

- Observations 的核心关系为 Observer 观察 FOI 并产生 Facts。Aspect、结果和适用时间由 Facts 表达。
- Facts 保留 Segment、Event、Measurement 三个家族；本轮只实现现有 Segment、Event 链路。Measurement 的业务例子用于验证模型，真实计步采集和家族实现不在本轮。
- 每条 Fact 有一个 Target。Target 相同不代表 Fact 相同，也不改变直接 FOI 的业务含义。
- App 是产品；应用上下文由设备与 App 共同辨认，跨应用启动保持。Profile 不参与应用上下文身份。
- 使用者关联由 Heartbeat 独立维护。账号属于当前 Owner 不等于已经证明该账号或设备在全部历史中由本人使用。
- 数据来源不建模为 DataSource；不添加其注册、身份、逐条强制描述或管理页面。

### 接口与模块

- 跨 Collection、Runtime、Analytics 的事实契约增加明确的 Observer 身份和单一 Target 引用。Target 使用种类和引用值的表达方向，不扩展成对象设备、对象账号、对象本人、环境设备、来源账号五列。
- 生产链路的引用必须能在离线时创建或继续使用。设备采用已有稳定设备依据，账号采用可取得的服务身份；应用上下文输入可利用设备身份和既有平台应用标识，由 Analytics 解析到 App 产品与其设备上下文。不得要求每次开始活动先在线换取数据库行号。
- Analytics 将输入引用解析到具体业务资料，事实持久化直接保存 Observer 与 Target 的关联。具体标识编码沿用可用的业务身份，在首次实施任务中统一，不另建通用身份注册体系。
- Observer 的稳定身份由运行管理层提供。System/Managed Collector 优先沿用语义匹配的持久采集实例身份；Browser 根据实际持久扩展安装身份建立稳定映射。不得用 Activation、进程或版本号作 Observer 身份，也不得因多个安装共用 App 上下文而合并它们的写入身份。
- 新发布接口允许一个 Observer 为不同事实提供不同 Target，不从实例上的旧 Subject 强行推断所有事实归属。
- 保留 SystemActivityModel、Browser 窗口模型及 Browser 最小 Segment SDK 的业务分工；它们不承担目标资料登记或 Analytics 查询。
- VRChat 将世界/实例连续性的业务判断与 Fact 身份、修订、快照机械处理分开。只提取本轮实际复用的行为，不以此建立完整跨语言 SDK。
- 传输 ACK、缓存保管、Gap 和生命周期管理继续履行原有职责；变更公共事实字段时同步所有实际承载与确认比较，不通过字段丢失或默认值绕过旧逻辑。

### 事实身份与迁移

- 发布编排采用 [ADR-058](../../docs/adr/058-ci-database-migration.md)：同一部署 CI 在停写和备份后
  独立执行候选镜像的 EF migration 与 C# 回填，成功后才启动新 Analytics；生产重启只检查版本。
  任务 05 按[迁移 Runbook](../../docs/runbooks/analytics-database-migration.md)演练此入口。
  1C1G 下迁移 SQL 无限等待、外围 CI 保留长时间上限；不再沿用十分钟停写门禁，记录实际耗时。
- 本轮解除 Stream 的业务归属职责，不修改已部署的 Fact 身份规则。保留现有 Owner、Stream、FactId 的身份组合及正常 Revision 语义；Stream 可以作为内部身份/交付信息存在，不能成为新业务查询的归属入口。
- Observer 身份、Target 归属与 Fact 唯一身份是三个不同问题。禁止把 StreamId 全局重命名为 ObserverId，禁止按相同 Target 归并事实。
- 追加数据库迁移，保留已有家族表及唯一 Payload；不修改已上线迁移，不复制完整事实到第二套表。
- 升级前后保住家族行身份、FactId、Revision、时间和 Payload。对当前 Runtime 缓存、Browser 待发快照及 VRChat 检查点的兼容从第一批开始设计，不留到最后才补。
- 旧数据只依据已保存信息映射。System 的已知设备归属直接映射；Browser 有设备与 App 依据的记录映射到应用上下文，缺少应用依据的历史记录保持已知设备粒度。
- 历史账号保留其可辨认的原身份；缺少服务内账号标识时明确保持未知，不用昵称或一次当前登录覆盖全部历史。旧 Observer 信息确实缺失时保留未知历史语义，不伪造具体 Collector。
- 同一旧快照经缓存升级、旧入口兼容或新入口重放，必须收敛到同一个事实；回填后的有效归属一致，不因为同 Revision 从旧形状换成新形状而重复插入或无故冲突。
- 新原生输入必须明确提供有效归属；历史未知元数据是存量信息限制，不作为新写入持续绕过规则的入口。

### 业务资料与查询

- 设备、账号、本人和应用上下文按具体业务资料组织；允许复用语义正确的既有资料，不把所有 FOI 登记到统一 Objects 表。
- 应用上下文按同一 Owner 下的设备与 App 唯一识别；App 解析继续尊重平台身份与产品的区别。已有产品合并/纠错流程要维护相应引用，不能制造悬空 Target 或合并 Fact。
- 账号记录保留服务及已观测到的账号标识。VRChat 的 App 展示或筛选通过明确的服务/产品关联解释，不伪造机器平台应用标识。
- 活动、输入及通用事实查询改用有效 Target；按设备查询同时涵盖设备 Target 与该设备的应用上下文 Target。
- Report 继续仅统计明确的 System 活动；Browser、VRChat 不因新归属而变成可累加的注意力时长。
- 使用者关联先实现设备、账号到本人的明确业务联系及其适用范围，不实现通用关系图。
- 本人查询纳入个人 Target 及符合时间范围的设备/账号事实，应用上下文经其设备参与关联。对部分覆盖的 Segment，查询只将关联覆盖区间用于本人展示或计算，原始 Segment 不被切写；Event 按发生时刻判断。
- 关系重叠时事实只返回一次。补充、纠正或移除关联只改变相应查询结果，不改写 Fact 的 Observer、Target、Payload 或 Revision。
- 查询和关联维护使用现有 owner 身份与管理入口，提供最小可用的设置/筛选交互。已有认证凭据、Collector Secret 和授权恢复不混入事实资料。
- 当前代码中 Source 表示观测者分类并服务既有视图；它不等于本轮排除的 DataSource，不能按名称相似顺手删除。

### 实施顺序与兼容退出

- 沿用用户已认可的五批：System 完整链路、Browser、VRChat、本人关联查询、切换清理。
- 第一批通过现有发布到 HTTP 摄入再到查询的最高可用 Interface 验证，并完成可持续兼容旧输入的增量迁移。必要的扩展准备与 System 功能同批，避免空的框架任务。
- Browser 与 VRChat 都依赖第一批公共契约；两者没有业务上的互相阻塞。本人关联查询依赖账号链路和应用上下文链路，确保首批目标覆盖全部已接入业务。
- 每批保持方案可构建、可验证；允许短期旧输入转换，但只向同一份家族事实写入。
- 兼容对象明确限定为本次改造前的第一方 Collector/Runtime 版本和其持久缓存。退出条件为三个 Collector 已迁移、原有缓存有已验证的处理路径、可映射存量完成回填、未知历史记录可直接查询。
- 最后一批移除旧业务归属回退与已无人调用的转换入口。仍有实际用途的传输 Stream 或旧管理授权概念不因同名被机械删除；其保留用途要明确，业务查询不能再依赖它们。
- 生产切换前完成既有数据快照演练、缓存重放与查询对照。遵循已有升级备份及停写窗口约定；没有实际发布授权时交付可审阅的切换结果与步骤，不擅自部署。

## Testing Decisions

- 好的测试验证外部行为：发布的事实在 HTTP 摄入、数据库持久化和公开查询之后，身份、归属、时间、内容与所有权保持正确。不为类名、包装层或与实现同形的分支补测试。
- 主测试 Seam 采用现有 Collector → Runtime → Analytics HTTP → 查询链路。既有 FactHttpTests 已能覆盖 Runtime 重启和未知 Payload 重放，优先扩展该入口。
- 既有 FactStoreTests 继续验证事务、重复/乱序修订、冲突与 Owner 隔离；增加 Observer/Target 的行为断言，而非另造一套只测 DTO 的验证框架。
- 沿用真实 PostgreSQL migration fixture；针对已部署分表基线追加升级测试，保持旧迁移本身可验证。验证新元数据回填、重复升级和快照重放收敛，不把历史固定列数断言简单删掉。
- System 验证 Segment 与 Event 的完整发布/查询路径、重启后的 Observer 身份及原有活动规则；平台相关场景保留精简可重复的 smoke。
- Browser 复用窗口、fold、Segment SDK 和协议集成测试，覆盖双窗口、跨启动同一 App 上下文、独立安装共用 Target 但不混淆写入身份、旧待发快照以及查询。
- VRChat 复用会话/采集集成与状态机测试，验证真实服务身份的传播、相同世界持续、切换世界与恢复；自动测试可用可控会话，另区分真实账号验证结果。
- 本人关联通过维护入口写入，再从公开查询读取，验证追溯生效、部分时间覆盖、重叠去重、纠错及隔离；不能仅测试一条关联行的增删。
- 全链路切换验证旧新两种快照表示不双写、不丢数据；相同 FactId 出现在不同旧 Stream 时仍是不同事实，相同 Target 不会被用作去重依据。
- 有代码修改时运行相应项目测试、解决方案构建及本地 .NET 命名检查；跨项目改造按仓库要求完成回归。纯规格与票据本身不运行产品测试。
- 每项实施采用 TDD 围绕风险推进，完成后进行 Standards 与 Spec 两轴 review，再提交。人工/真实资源尚未完成时如实保留相应状态，不把自动测试结果扩大为全部验收。

## Out of Scope

- DataSource 及相关独立身份、注册、字段或配置面板。
- 统一 Objects、ObjectRelations、共享 ObservationContexts 存储层，或所有临时 FOI 的全局登记。
- 五种角色分别铺开五列、通用 Aspect 目录、Fact Schema 注册/版本/哈希治理。
- 改造 Fact 唯一性、保存所有修订、撤回机制，或再建通用 Facts 表复制内容。
- 微信步数 Collector、Measurement 家族表及完整 API；仅保留已确认概念与未来兼容位置。
- 完整公共 SDK 发布、所有协议版本/能力/制品校验的重新设计；本轮只调整新事实关联所需接口。
- 凭账号在线推断 Quest 使用、从页面选中推断用户注意力、自动推断使用者或恢复无证据历史。
- 未经授权的生产部署、现有数据清空或重写已上线迁移。
- 新建通用对象管理产品界面、通用关系编辑器或无需求的 Profile 独立档案。

## Further Notes

依据为已收口的 Observations 与 Facts 模型基线、Shared Kernel 术语、ADR-056，以及 ADR-055 的家族存储与已上线迁移约束。早期讨论文档仅供历史参考，不能恢复已撤回的方案。

已有“观测模型改造”与“Browser 最小 Segment SDK”是已完成的独立范围，不重开它们。本规格是后续观测身份与事实归属的实施工作；建立规格时尚无实现，当前进展见各任务的验收记录与下方实施备注。

采用用户已认可的端到端验收口径和五批顺序；任务的依赖以真实的阻塞关系表达，不要求 Browser 与 VRChat 人为串行。每项任务完成时更新其验收证据；整个功能在所有任务及实际剩余门禁完成前不标 done。

## Comments

2026-09-10：用户授权执行 to-spec 与 to-tickets。模型已收口，本规格不再追加业务访谈；实现者依据上述行为与迁移约束决定局部类型、表名和引用编码。


2026-09-10（任务 01 实施）：System Observer/Target 公共契约、Runtime 升级、追加家族迁移与
HTTP 查询已完成自动验证；任务 01 保持 ready-for-human，等待真实窗口/输入与 Windows 现场 smoke。
公共契约已具备，后续 Browser/VRChat 可据此实施；本 PRD 仍有 02–05 的开发与切换门禁，不标 done。
局部字段/引用和兼容消费者依据统一见 [System 实施记录](../../docs/architecture/system-observation-targets.md)。

2026-09-10（项目背景与后续承接）：用户要求记录上述项目规模、观测范围扩展和存储的重要性，
并准备任务 02 的启动提示词。01 的公共链路可供 02 使用，真实 System 门禁仍保持可追踪；
02 按已有验收要求解决应用上下文唯一性、离线引用、设备/App 查询和产品合并后的引用维护，
不能将本批正确性问题推迟到 05。05 承接整体迁移演练和旧归属路径清理。
用户在概述核心模型时再次提到 DataSource；此处记录该表述，不将其视为修改已确认设计的授权，
当前规格仍按“DataSource 暂不纳入”实施，若需纳入须先确认设计变更。

2026-09-10（任务 02 实施）：Browser 扩展安装 Observer、设备/App 应用上下文、增量迁移、
引用完整性、产品合并/纠错、设备/App 查询及旧缓存/快照重放已完成，任务 02 标记 done。
自动验证为 .NET 1,284 项、Browser 111 项、前端 289 项全部通过，并完成真实 Chrome headless
双窗口→macOS Desktop→独立 Analytics 的查询与离线重启补传验收，以及 Standards/Spec review。
实现及兼容退出依据见 [Browser 实施记录](../../docs/architecture/browser-observation-targets.md)。
真实结果不扩大为人工可见窗口、独立多 Profile、Windows/Edge 或 System 输入验收。
01 继续保持 ready-for-human；03/04 待实施，05 承接生产副本演练与旧路径退出。
本 PRD 仍为 ready-for-agent，保留上述具体真实门禁，不标 done；DataSource 仍不纳入。
