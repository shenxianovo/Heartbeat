# 09 — 删除旧主路径并完成整体验收

**What to build:** 所有实际生产、保管、存储和消费路径以 Observations 新模型工作，迁移期临时形式已清理；完整回归证明独立契约、历史保全和展示分析共同成立，留下可核验的完成状态。

**Blocked by:** [04 — System 活动与输入全链路切换](04-system-observation-cutover.md)、[05 — Browser 多窗口全链路切换](05-browser-observation-cutover.md)、[06 — VRChat 账号观测全链路切换](06-vrchat-observation-cutover.md)、[07 — 对象关系与产品维护贯通](07-object-relations-and-products.md)、[08 — 分析与 Dashboard 消费新事实](08-analysis-and-dashboard.md).

Status: ready-for-human

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [ ] 所有前置任务验收完成并记录证据；01–03 通过依赖传递纳入，不因存在新入口或部分生产者已迁移而宣布本轮完成。
- [x] 全库盘点 Collector、SDK、协议、Runtime、缓存、HTTP、实体/数据库、查询、分析、Dashboard、脚本及测试的活跃路径，新事实不依赖旧 Subject/Stream 决定身份、Kind、FOI 或完整性。
- [x] 在调用方迁移完成后收缩共享接口，删除无消费者旧主路径、临时桥接及仅验证已退役行为的测试；真实旧数据转换进入统一核心，不维持两套完整事实存储或永久新转旧路径。
- [x] 保留的交付分组、管理身份、历史迁移和兼容资料各有真实消费者及职责，兼容台账记录支持状态、退出门槛、验证方式和承接者；不能机械删除仍需恢复的数据能力。
- [x] System、Browser、VRChat 的实际发布与新存储、对象维护、分析和 Dashboard 联合回归通过，包含同 App 多设备、多窗口、账号无设备、人工关联及产品纠错后的重放。
- [x] 独立原生契约、必填和固定属性、同版本冲突、乱序、增长/缩短、事务回滚、Owner/Id 隔离、未知结果保全在最终组合中成立。
- [x] 旧数据库/缓存升级、进行中事实、包回退保护、断线重启、迟到 ACK 与 Gap 在最终组合中保管正确；旧行身份与完整内容保持，新增变更的迁移重试验证通过。
- [x] 执行改动对应回归和仓库规定的全仓检查，并完成规范与规格审查；失败先定位修复，最终证据明确针对当前候选，不直接复用上一轮测试数量。
- [x] 核对父规格全部验收和 30 条用户故事的覆盖，记录每组结果及具体缺口；源于本次改动的文档、词汇、ADR、验证入口和兼容记录漂移完成收尾。
- [x] 区分代码实现、自动验证、真实安装和生产验收。仍需人工门禁的事项有可重复步骤与明确承接者，相关任务保持 ready-for-human；本任务不得将未完成验收标 done。
- [x] 业务库迁移、部署及暂停的完整副本资源/恢复演练保持父规格的范围，由原存储任务承接；不把本轮自动验证当成生产验收。
- [ ] 在实施 closeout 中按仓库 lifecycle 规则同步已完成子任务与父规格状态；只有本轮所有必需验收完成才结束本轮实现。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。

### 2026-09-11 实施收尾与最终组合验收

固定基线 `2dc5007eb263cb9698b03daea02593b34b67dfcb`；开始时 HEAD 与指定基线相等、工作区干净。
独立分支 `codex/observation-ticket-09`，工作区 `/Users/bytedance/.codex/worktrees/8cad/Heartbeat`。
仅维护本票、必要实现/回归和权威文档；父 PRD、ORCHESTRATION、01–08 状态由协调任务维护。
01/02/03/07/08 已完成；04/05/06 实现已合入但真实安装门禁仍为 ready-for-human。
本节的代码与自动验证证据不将“所有前置全部验收”勾选，也不代表全轮或生产完成。

#### 本票代码差异

- 修复实际 Headless 原生读模型：旧代码直接访问 null Stream，异常被 Runtime 的非持久观察回调捕获，
  导致事实上传正常但当前活动不更新。公开 pipeline 当前活动回归先稳定 NRE，再暴露新 Result 的
  activityKey 被当成旧 identityKey 读取而退回 Fact UUID；两项分别修正，完整回归保留。
- Runtime 将管理 InstanceId/Subject 显式传给可重建读模型，原生按 DeliveryInstanceId 取实例，
  历史按原 Stream 取实例。Observer 和 FOI 不因此改变；重启回放可在管理 Attach 前重建当前活动。
  公开 Runtime 发布/重启测试使用不同 Observer 和 Instance，验证两种身份不混用。
- 删除无消费者的 InputEvent projection/replay sink、空壳 projection fence、System DI 和对应写入口；
  fence 直接复用持久提交契约。连同审查收缩，退休9项只测旧写入口的用例，新增1项缺publisher拒绝回归；
  另2项改为真实旧缓存 seed→上传/重启，归一化与Windows/macOS输入测试观察publisher发布。
  旧 input 缓存及 receipts、segments-cache 排空仍保留，不能把新事实投影写回这些旧队列。
  InputBuffer不再保留新写旧队列/容量机制；无publisher的构造仅供历史读取，实际输入先明确拒绝。
- 删除无调用的 BrowserFactAttribution.Target、PersonReference.ToTarget、Registry.Touch/Discover
  及对应实现/fake。保留实际声明上行、Source 状态与 Enabled 消费者。
- 本人 UI smoke 的新数据种植改为 `/observations`，不再虚构 Subject/Stream；原 Id/Kind/Result
  契约显式且读回检查旧交付键为 null。脚本依赖本地真实开发凭据，本轮不执行凭据/真实账号操作。
- 模型、交接、glossary、ADR-040/041/055/059 与兼容台账区分历史诊断和当前实现；版本引用通用
  Runtime9/SDK4 与各生产者专有权威，消除“生产者尚未切换”及强制 Stream 的现状误述。

#### 活跃组件与保留边界盘点

| 组件/契约 | 当前主路径或实际消费者 | 保留边界、退出条件与承接 |
| --- | --- | --- |
| System AppMonitor、输入、NDJSON ingress | 新 Id/Kind/Collector/机器 FOI/Aspect/Result；空 Binding 原生发布 | ingress2；两个 Stream 只服务真实 Gap 与旧条目。04 owner Windows/macOS 权限安装、旧文件排空及窗口结束 |
| Browser fold/SDK/protocol | 持久扩展 UUID；App FOI、准确 observed-on；多窗口单调 Revision | journal5、旧 key fence/facts.recover 仅精确旧键高水位/收尾；05 owner Chrome/Edge 全 Profile 清单和窗口。见专有 cache-compatibility.md |
| VRChat presence/ManagedProcess | 明确账号 FOI、空关系、持久 Observer；实际 stdio→SDK→Runtime | checkpoint4；v1–3 旧未知账号正常结束，不由当前账号接管。06 owner/Headless维护者真实 linux-x64 安装和窗口 |
| SDK / Collector Protocol | Kind 显式选择独立快照；Revision 与完整 ACK；新内容不用旧时间派生版本 | SDK1–3 的 outbox/dead-letter、进行中 Binding/FactId 保持；SDK4 写入后不降版。04–06安装/备份排空并结束离线/回退窗口 |
| Runtime / 缓存 / Package门禁 | Kind原生分支直接保管；DeliveryInstanceId只管交付；独立HTTP与完整确认 | Runtime1–8→9、已ACK未final、Gap/alias/LKG；collector-data-requirements.json通用能力约束。现场窗口前不删除 |
| Headless / Desktop 宿主读模型 | 原生当前活动按新 Result；管理身份独立传入；不参与持久写入 | 旧 FactUploadReadModel.SegmentId 仅历史 Stream+Fact 视图；旧input/segment cache与receipts正常排空，04/06与原发布门禁 |
| HTTP / 保存核心 | `/observations` 完整校验→唯一Facts核心；Id/Kind/Collector/FOI/Aspect固定 | `/facts`及segment/input import仅服务真实旧缓存；明确转换完整旧Owner/Kind/Stream/FactId，原行Id/Revision不变；旧流量/待发归零和窗口后退出 |
| DB / 追加迁移 | 五表、原生旧键可空、准确Fact关系与完整Result；不新增平行存储 | 历史迁移及TargetKind/TargetId旧列/index仍在schema历史边界，当前写入只清null、归属查询无消费；物理删除需追加迁移及原存储上线/恢复验收，不改已发布迁移 |
| Query / 产品维护 / 本人 | FOI及准确Fact关系；AppReferenceEvidence原平台依据；used-by独立 | App/Device数字ID仅产品资料/筛选别名；旧查询DTO Stream/FactId nullable。产品维护不是Collector纠错平台 |
| 分析 / Recap / Question | 按Aspect解释；Source未知保留；Question payload3/Recap契约hash惰性失效 | Source继续深度声明/Matcher职责；不批量调用真实LLM、不猜未知Aspect |
| Dashboard / OpenAPI | FOI UUID、准确设备关联、多窗口泳道、未知JSON无损与nullable Source | Payload/Result展示别名有API/视图消费者，不能直接删；事实与展示不复制成新Fact |
| scripts / fixtures | 本人smoke新入口；Browser真实临时Profile；System实际旧loader回退验证 | migration rehearsal SQL是历史前后映射消费者；Reference旧默认发布是协议/生命周期兼容fixture，不是第四个业务Collector |

完整格式、真实消费者、owner及退出验证以 [通用缓存矩阵](../../../docs/architecture/observation-cache-compatibility.md)
和[兼容台账](../../../docs/architecture/compatibility-debt.md)为准，Browser 版本只在
[专有台账](../../../collection/collectors/Heartbeat.Collector.Browser/cache-compatibility.md)维护。

#### 30 条用户故事覆盖映射

以下入口均是本仓真实测试，不以单一手写HTTP fixture代替联合验收。最终全仓包含这些测试；
定向复验与全仓重叠，不相加充当总量。对应现场限制在最后一列单独列出。

| 故事 | 自动验证入口 / 观察到的行为 | 现场/范围限制 |
| --- | --- | --- |
| 1 观测完整信息 | FactHttpTests.Independent：原生独立身份、对象、结果与家族时间原样GET | 无新增现场项 |
| 2 无旧交付骨架 | NativeCollectorInitializesWithoutLegacySubjectOrStreamsAndSurvivesRestart；3真实生产链 Stream=null | 安装归04–06 |
| 3 Id贯穿 | SystemObservations、BrowserFold、VRChatNativeManagedProcess：生产Id→Runtime→PG→GET保持 | 安装归04–06 |
| 4 Kind自身声明 | Independent segment/event、NativeDelivery与协议原生测试，不按Stream推导 | Measurement仍无新业务契约 |
| 5 独立对象 | IndependentObservation_AllObjectKindsNeedNoDeviceRelation；真实VRChat无设备 | 真实账号归06 |
| 6 未知内容 | Independent/Aspects未知Aspect和任意JSON；Frontend factViews/API客户端roundtrip | 不增加schema治理 |
| 7 缺必填拒绝 | IndependentObservation_RejectsIncompleteNativeInput、协议NativeDelivery拒绝矩阵 | 无 |
| 8 历史未知 | Cutover未知对象、LegacyObservationMigration、VRChat旧checkpoint矩阵 | 真实历史库迁移归原存储 |
| 9 固定身份 | IndependentObservation_RevisionOrdersSnapshotsAndProtectsFactIdentity；Runtime同Fact验证 | 目录维护仍为既定产品操作 |
| 10 变化新事实 | System转场、Browser关窗重开、VRChat账号/位置轮换发布测试 | 现场归04–06 |
| 11 持续增长 | System与Browser真实publisher、VRChat ManagedProcess持续修订 | 同上 |
| 12 合法缩短 | System真实15秒修订、Browser同End内容/缩短final；独立HTTP顺序测试 | 无 |
| 13 Revision顺序 | Browser单调持久版本、SDK/Runtime与Independent乱序矩阵 | 旧高水位只在兼容边界 |
| 14 同版幂等冲突 | Independent/协议完整语义比较、Browser dead-letter保全 | 无 |
| 15 固定家族时间 | Independent segment/event时间拒绝、System起点和VRChat恢复End保全 | 无 |
| 16 离线重启 | System ingress/Runtime、Browser真实Profile进程重启、VRChat杀子进程/重启 | 真实安装归04–06 |
| 17 精确ACK | 三实际publisher迟到HTTP ACK；Runtime同版IsFinal/完整内容拒绝 | 无 |
| 18 进行中与Gap | System实际容量Gap；Browser旧无高水位恢复；VRChat legacy1–3矩阵；通用cache矩阵 | 现场旧状态全量清单缺 |
| 19 旧包拒绝 | System实际旧loader脚本；Browser历史loader/fence；ManagedProcess marker启动/候选/LKG测试 | 所有已安装包清单及回退窗口缺 |
| 20 Browser并行 | BrowserFold真实两窗口、close/reopen/SW和Chrome进程重启；Frontend lanes | owner Profile归05 |
| 21 System真实链路 | win/mac参数的AppMonitor→ingress→Runtime→HTTP/PG；Input和Gap | 真Windows及macOS权限安装归04 |
| 22 VRChat真实链路 | 真实apphost/stdio双账号崩溃恢复→HTTP/PG、无设备本人关联 | linux-x64真实账号安装归06 |
| 23 历史唯一对应 | Legacy/IndependentObservation migrations；旧导入先到/重放先到；Browser准确旧行GET | 业务库迁移归原存储 |
| 24 关系原子 | IndependentRelations_InvalidLaterFactRollsBackEarlierRevisionAndNewFact；准确Evidence和时间修订 | 无 |
| 25 同App多设备 | 本票System真实两设备15+10=25秒，分设备报表及本人只含第一台；Browser准确device/app过滤 | 实际安装归04/05 |
| 26 Owner隔离 | IndependentRelations_PrivatePersonAndFactCollisionDoNotExposeAnotherOwner及独立HTTP/迁移跨Owner矩阵 | 无 |
| 27 产品维护重放 | 本票真实Browser两窗口目录重绑→原生产快照replay；IndependentProducts合并/拆分/fallback矩阵 | 不做通用FOI纠错 |
| 28 本人关联 | 本票实际System/Browser设备关联、VRChat单账号关联/移除；IndependentPerson矩阵 | owner UI凭据smoke未执行 |
| 29 分析展示 | 实际System daily/usage；Browser和VRChat公开回放/本人GET；Analysis/KnowledgeIndependent；Frontend verify | 未批量真实LLM或宣称真实Dashboard安装 |
| 30 可判定验收 | 本票组件/兼容/30故事矩阵、最终候选全仓与双轴审查 | 前置现场未全验收，保持ready-for-human |

#### 现场与 lifecycle / friction closeout

- **04 owner / Desktop维护者**：按04的安装步骤，在真实Windows及macOS授权后检查前台/输入，
  断网→应用重启→重传→读取同Id/正确Revision，核对真实Gap及权限提示。缺真实Windows和macOS权限安装证据。
- **05 owner / Browser维护者**：按Browser专有台账，逐Chrome/Edge真实Profile备份旧keys/journal，
  升级、双窗口、SW与整浏览器重启、离线重放、准确旧final/ACK；核对原扩展UUID与App/device关系。
  临时Profile smoke不是owner全部Profile验收。
- **06 owner / Headless维护者**：按VRChat README，在真实linux-x64安装包与逐账号目录备份后授权，
  断线/进程重启/重新读取，核对账号FOI、旧未知账号和Gap；在隔离目录验证不兼容候选/LKG拒绝。
  本票仅使用mock账号和当前macOS/arm64 apphost。
- **原 observation-storage 发布/迁移任务，owner + Collection/Analytics维护者**：收集每个实际
  Package/version/contentHash、数据目录schema、pending/final/dead-letter数量和最老待发时间，
  明确最长离线及回退窗口并确认结束；业务库迁移、完整副本资源/恢复演练仍暂停，未执行也未另建backlog。
- 本票发现的宿主读模型旧依赖、无人消费接口和权威文档漂移在本轮处理。上述现场缺口已有04–06
  与原存储任务承接；兼容reader继续保留。父PRD/其他票的状态协调由指定协调任务执行，09不越权修改。

#### 验证记录与独立审查

首轮完整候选树 `2e37d807530f3dc854883c770c5ce6d70e46dc00`（完整索引树，未创建进度提交）：

- `dotnet tool restore`、`dotnet restore Heartbeat.slnx`、基线及候选 `dotnet build Heartbeat.slnx --no-restore` 成功。
  基线有30条已有warning；首轮最终增量构建有16条已有warning、0错误，不把增量日志当无warning证明。
- `dotnet test Heartbeat.slnx --no-build --nologo`：13项目 **1,475/1,475 PASS，0失败/跳过**，
  包含服务端隔离PG657、Hub347、SDK59、System89、VRChat49、Headless14、Windows38、Mac82。
  日志 `/tmp/heartbeat09-dotnet-all.log`。这轮之后审查要求继续收缩InputBuffer，最终复验另列。
- `npm --prefix frontend run verify`：typecheck、**308/308** 与build PASS，日志 `/tmp/heartbeat09-frontend.log`。
- Browser `npm run build`、`npm test` **146/146**、`node scripts/collector-contracts.mjs check` PASS；
  dist与跟踪Package的5个文件逐字节相等，普通test及临时Profile smoke后没有Package diff。
  日志 `/tmp/heartbeat09-browser-build.log`、`heartbeat09-browser.log`、`heartbeat09-contracts.log`。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` PASS；
  `node --check scripts/smoke-person-associations.mjs`及diff检查PASS。本人UI脚本依赖真实开发凭据，
  本轮没有执行，不能以语法检查充当真实Dashboard操作；本人HTTP组合和Dashboard组件分别有通过证据。
- `python3 scripts/verify-system-ingress-rollback.py` PASS：编译执行 `e6564fc` 真实旧loader，
  input/checkpoint/reset-only三种新日志均拒绝，文件集合与SHA256不变；当前loader恢复全部并以
  观测边界和Revision10收尾。日志 `/tmp/heartbeat09-system-rollback.log`。
- `node scripts/smoke-browser-observation-cutover.mjs` PASS，完成于 `2026-09-11T15:30:30.173Z`；
  fresh临时真实Chrome Profile验证双窗口、关闭重开、SW、Runtime、整浏览器/Host重启和离线重放。
  日志 `/tmp/heartbeat09-chrome.log`，报告
  `/var/folders/ns/7njj05zs4x70mm21q4fdznbw0000gn/T/heartbeat-browser-cutover-QLbDHJ/report.json`。
  `productionProfileUsed=false`，Observer `ee1ac752-8014-4532-ba0f-6545d32b23b0`；
  临时development包hash `sha256:2b21ca957ee7174efc76c3a94eb9a15fee02b4576612cfe2b14b5bb573a795b9`，
  此安装不是owner真实Profile全量盘点，PG后半链由同生产逻辑的FactHttpTests.Browser继续证明。

Headless RED证据：`/tmp/heartbeat09-headless-red.log`为空Stream NRE；
`/tmp/heartbeat09-headless-identity-red.log`为activityKey错误退回UUID；修复后Headless14项与最终全仓通过。
最初新增Runtime断言的nullable编译错误已在全仓前修复；局部共享构包并发EEXIST及Browser缺esbuild是
环境/接线失败，不记作业务RED。最终构建和全仓记录来自修复后的源码。

Standards 初审：0硬标准违反、0可行动smell；确认管理身份独立、旧cache/receipt未丢、无具名宿主特化。
Spec 初审：1项P2，InputBuffer未提供publisher仍可新写旧队列，仅有测试消费者；已接受并在本票修复，
保留真实旧缓存读取/确认。复核与最终候选验证见下文。

审查修复后的全仓第一次重跑为 **1,471 PASS / 1 FAIL / 0 SKIP**：唯一失败是既有 SDK
`NonRetryableGapRejectionBecomesOneDurableDiagnosticWithoutHotLoop(duringDrain: true)`，100ms墙钟
期限在文件IO期间耗尽而返回FlushCancelled/DeadlineExceeded，日志 `/tmp/heartbeat09-dotnet-reviewed.log`。
单独重跑SDK59项通过后，仍修测试根因：该测试并非验证期限到期，改用既有VirtualTimeProvider统一
Client/Binding的协议时钟，保留外层5秒watchdog、单次report、准确GapId、持久死信和outbox排空断言。
不改变生产期限或保管逻辑；修后SDK59项再次通过，日志 `/tmp/heartbeat09-sdk-clock.log`。
最终全仓结果另列，不把失败初跑算全绿。

InputBuffer审查修复先RED（新输入无publisher本应拒绝但没有抛出异常），再删旧新写队列与容量分支，
修后System86、Windows输入5、Mac输入11通过。实际工具输出摘录（非完整stdout）为
`/tmp/heartbeat-ticket09-input-publisher-verification.log`。本轮共退休9项旧写入口测试，新增1项
缺publisher回归及1项Headless回归；最终计数变化不表示漏执行测试。

## Standards

固定基线 `2dc5007` → 最终实现树 `5d68b974b357c2ed97a7e1a9cafdfeec3119c34b`，独立只读复核
**0项硬标准违反、0项可行动smell**。管理实例与Observer/FOI分离；新输入只走publisher，真实旧
文件/receipt读取确认保留；SDK测试采用已有虚拟时钟，持久保管断言未弱化。最终树之后仅补本票
状态/验证证据，不为审查创建进度commit。

## Spec

独立初审1项P2（InputBuffer仅由测试维持新写旧队列）已修复并由原审查者关闭；最终实现树
`5d68b974b357c2ed97a7e1a9cafdfeec3119c34b` 复核 **0项未解决发现、无scope creep**。
真实Browser维护后当前版及旧版生产快照到PG重放、三个生产者本人/设备/分析组合已覆盖。
SDK时间确定性修复保留测试目标。04–06及原存储现场门禁仍未验收。

审查合计：Standards 0发现；Spec 1已关闭、0未解决；两轴均无剩余最高风险项。

### 最终候选验收与交付状态

- 最终代码树 `5d68b974b357c2ed97a7e1a9cafdfeec3119c34b` 与最终审查一致；其后仅追加本票证据/状态。
- 最后一次 `dotnet test Heartbeat.slnx --no-build --nologo`：**13项目、1,472/1,472 PASS，0失败、0跳过**。
  服务端657、Hub347、SDK59、System86、VRChat49、Headless14、Windows38、Mac82等全部通过；
  System两设备、Browser当前版/旧版产品维护后重放、VRChat双账号本人组合和所有历史迁移矩阵在此候选重跑。
  日志 `/tmp/heartbeat09-dotnet-final.log`，SDK当前测试代码已重新build并通过；solution审查后build成功，
  最后增量build日志 `/tmp/heartbeat09-build-reviewed.log` 中4条既有server测试nullable warning、0错误。
- 最终命名检查PASS，日志 `/tmp/heartbeat09-style-complete.log`；完整工作区/索引diff检查PASS。
  Frontend308/type/build、Browser146/build/contracts、真实临时Chrome及System旧loader证据仍对应
  最终生产代码；此后只有System旧fallback清理（在最终.NET全仓覆盖）及SDK测试时钟修正，不重复无改动检查。
- **代码实现完成、自动验证完成；真实安装与生产验收未完成。** 09保持ready-for-human。
  第一项“所有前置任务验收完成”保持未勾；末项全轮lifecycle由协调者在04–06及原门禁完成后处理，
  按委派边界未修改父PRD、ORCHESTRATION或其他票，已向协调任务交接本票结果与未完现场项。
- 未push、deploy、执行业务库迁移、访问真实VRChat账号或恢复暂停的完整副本/资源演练。
  仅清理自身fixture子进程和隔离资源。正常Conventional Commit包含实现、必要回归、权威文档与本票证据；
  没有纯定时进度commit或为审查清工作树而造的提交。
