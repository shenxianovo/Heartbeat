# Observations 实施协调

2026-09-11，用户授权本会话组织九项实现并创建编码会话。

协调会话：`01a08fb5-a1f8-7f21-8155-d479ab3a25f4`。
规格：[PRD](PRD.md)。每项范围、验收和依赖以对应 issue 为准。

## 当前结论

本轮所有代码实现已提交整合至main `0f11c92`；没有待运行的编码任务。协调工作区全仓.NET 1472/1472通过，最终候选Frontend308/Browser146及类型/构建通过。01/02/03/07/08 done；04/05/06/09与父PRD因明确现场门禁保持ready-for-human，不能宣布全轮现场验收done。自动跟进仍PAUSED，不自动恢复。仅父PRD与本记录未提交，按用户要求不作纯进度commit。后文按时间保留过程记录，其中“全局不提交”等旧解释已被后续纠正覆盖。

## 执行方式

- 编码会话在独立 worktree 工作，按 implement → TDD → code-review → commit 完成本项；实际实现、测试和本票验收正常提交，不创建纯进度提交。
- 协调会话检查验收及实现提交，整合到本地 main；后续任务使用真实实现提交作为工作树基线，不推送或部署。
- 编码会话维护自己的 issue 与实施证据；父 PRD 和本协调记录由协调会话维护。
- 用户明确：定时跟进只修改协调记录/进度文件，不暂存或提交；不得为了清空工作区而提交这些记录。
  整合实际实现提交时保留未提交修改。已撤销三个进度提交，文档内容仍保留在工作区。
- 实现依赖经代码审查、自动验收与整合后才能启动依赖实现。02/07/08并行，03后04–06并行；04–06代码自动验收已整合，可启动09中不依赖现场的代码收尾。未完成现场门禁仍阻止全票/全轮done，09不得将前置全部验收提前勾选。
- 当前已授权范围不含业务库迁移、生产部署和暂停的完整副本资源/恢复演练。
- 自动跟进 `observations` 配置为每 5 分钟；当前读取状态为 PAUSED，保留暂停，不自动恢复。启用时只报告有意义的进展，全部验收后停用。
- 同时最多推进三个编码会话；新会话使用项目默认模型，不覆盖用户设置。

## 任务登记

| 任务 | 依赖 | 编码会话 | 进度 | 已整合提交 |
| --- | --- | --- | --- | --- |
| 01 独立观测保存与读取 | 无 | `01a09012-2096-7ee0-b81b-9b040d0ce760` | 已验收整合 | `6976e10` |
| 02 原生观测的持久保管与交付 | 01 | `01a0902a-288f-7233-9349-7d07fa829cc2` | 已验收整合 | `da6dd3f` |
| 03 旧事实与旧缓存无损接管 | 02 | `01a090d8-3574-7cf3-976c-243cbd1b8ed7` | 已验收整合 | `e6564fc` |
| 04 System 活动与输入全链路切换 | 03 | `01a090ea-adb9-7c10-a3da-1b2c66fb0c95` | 实现已整合且复验通过；现场待人工 | `2feff12` |
| 05 Browser 多窗口全链路切换 | 03 | `01a090eb-0891-73a2-8563-4e8869587e02` | 实现已整合且联合复验通过；现场待人工 | `eb4e921` + `2dc5007` |
| 06 VRChat 账号观测全链路切换 | 03 | `01a090eb-0894-7f02-a5bb-26b9ae853360` | 实现已整合且复验通过；现场待人工 | `8cd2df2` |
| 07 对象关系与产品维护贯通 | 01 | `01a0902a-7a54-7072-ac1d-a8c28e1047cd` | 已验收整合 | `da6dd3f`（来源 24e968a） |
| 08 分析与 Dashboard 消费新事实 | 01 | `01a0902a-d579-7040-bb6c-187301e73d76` | 已验收整合 | `da6dd3f`（来源 adcec27） |
| 09 删除旧主路径并完成整体验收 | 04、05、06、07、08 | `01a0910d-0d89-7272-8d3f-b777eea85697` | 代码已整合、自动验收完成；现场待人工 | `0f11c92` |

## 当前进度

最新规则（用户再次明确）：只是不提交定时进度；实际实现正常提交。02/07/08已合并提交da6dd3f，03切换此提交继续；以下历史中的“全局不提交”是协调者误解，已撤销。

规格和九项任务已创建；实现前代码基线为 `6526e13`，共同规格提交为 `e189ccf`。
01 已通过 create_thread 派发，worktree 为 `/Users/bytedance/.codex/worktrees/3ac9/Heartbeat`，
初始 HEAD 为 `e189ccf`，分支 `codex/observation-ticket-01`。
01 最终将实现及验收整理为单个提交 `bcb475caad6e80ccb616e59612da68249aedcb7d`，已整合为 main 的 `6976e10`。
编码会话报告：全仓 .NET 1367 通过，最后修复后相关154项与132项回归通过（组间重叠），双轴审查无遗留发现。
协调会话核对提交、核心/迁移及issue证据，并在整合后的main重新构建运行独立观测/关系/迁移专项：34/34通过。
复验命令：`dotnet test server/Heartbeat.Server.Tests --no-restore --filter 'FullyQualifiedName~IndependentObservation|FullyQualifiedName~IndependentRelations' --nologo`。
辅助日志：`/tmp/heartbeat-observations-01-integrated-tests.log`。PRD/协调记录的未提交修改保留，未作进度提交。
01 最后 wait_threads cursor：`46ad96b0-7d17-47c9-b954-890f1b6e048d:4`，会话idle/turn completed。

01 稳定接口为 `ObservationUploadRequest`/`ObservationSnapshot` 与 `/api/v1/observations`，
结果读取新增Kind/Result并保留Payload alias，旧交付键和Source可空；新旧入口共用SaveSnapshot核心。
02已获本地IsFinal/准确ACK交接；07需保留最小AppReferenceEvidence及当前App引用适用范围；
08承接nullable Source/Stream的所有派生消费者。07/08如交叉修改PersonFactQuery、DTO或生成client需协调。
02、07、08均从已验收基线6976e10派发，最多三个并行任务已占满；待正式ID出现后登记并用wait_threads跟进，
不得重复创建。clientThreadId不能用于要求threadId的工具。03仍等待02验收整合。

07/08 交叉文件已分工：07 负责 PersonFactQuery、PersonSourceCount 和必要的 Person 手写 UI；08 负责其他分析 DTO 及最终生成 frontend client，整合时核对契约。

### 07/08 文件整合与提交规则收口

为严格落实用户“改了就行，别提交”，后续编码和协调整合均不创建新 commit。02 已收到更新并撤回自己的实现提交，保留改动；已有07/08来源提交保留，但协调以 --no-commit 应用后取消暂存。main HEAD 仍为6976e10，原协调文档保留。自动跟进提示已同步，仍保持 PAUSED。

07/08 单票测试与双轴审查证据分别见其 issue。共同工作区已通过22项 IndependentPerson/Products/Relations/Analysis 专项（0失败、0跳过）；日志 /tmp/heartbeat-observations-07-08-integrated-tests.log。最终Person nullable Source client正在08工作区按真实OpenAPI重新生成，随后统一前端验证。03继续等待02完整验收。

07/08联合收口完成：从08合并07后的真实OpenAPI生成client并复制，PersonSourceCount.Source契约同步；main工作区22项关系/本人/产品/分析专项及2项原生知识缓存专项通过。frontend verify：typecheck、45文件308测试、生产构建全部通过；git diff --check通过，暂存区为空，HEAD仍6976e10，未新增commit。日志：/tmp/heartbeat-observations-07-08-frontend-verify.log、/tmp/heartbeat-observations-07-08-knowledge-tests.log。

### 02验收与03派发

02按最终HEAD diff导出 /tmp/heartbeat-observations-02-reviewed.patch，无冲突应用到协调工作区；未创建commit。协调复验服务端116/116、Hub322/322、SDK51/51通过，覆盖与07/08合并后的共享校验和读取；日志 /tmp/heartbeat-observations-02-07-08-server.log、/tmp/heartbeat-observations-02-hub-integrated.log、/tmp/heartbeat-observations-02-sdk-integrated.log。02单票最终全仓1385及双轴审查证据见issue。

03已经派发；以main HEAD6976e10加完整实现补丁作为起点，不提交基线。补丁 /tmp/heartbeat-observations-through-02-07-08.patch，SHA256 c536436f2b3f5cbfd8fbc078168aeff46e9c250050b7256240ccd4d1f3186801；元信息同名.json。稳定基线文件树3210b95de19513480e9635af00fe2671ee9493f9（非commit，临时索引生成，不改实际暂存区），含78个实现/测试/issue文件，排除父PRD/协调记录。03需基于此树交付自己的增量文件补丁；04–06继续等待03。当前仅03为活跃实现票，07/08/02完成。

### 用户纠正提交边界：恢复正常实现提交

用户明确“那你commit吧。你不commit后面工作树怎么办”。已将已验收02/07/08的78个实现/测试/本票issue文件提交为da6dd3f3e1976a7f661264caa3d48847da5b3abe，文件树与已验证3210b95de19513480e9635af00fe2671ee9493f9完全一致，未混入父PRD或协调记录。当前main仅这两份进度文件未提交。

03正式任务01a090d8-3574-7cf3-976c-243cbd1b8ed7，工作区/Users/bytedance/.codex/worktrees/0a0f/Heartbeat；已通知其在核对旧HEAD/保存增量后以mixed reset更新到da6dd3f，保留全部实现文件及子Agent工作，后续恢复正常实现commit。自动跟进规则已同步，保持原PAUSED状态。上述临时补丁流程不再作为后续任务的常规入口。

### 03确认与09文档交接

03已确认安全对齐da6dd3f，全部增量保留，子Agent已同步正常实现提交规则。03报告的待09核验文档漂移：observation-semantics.md“持久化与升级”仍写facts.observation:[1]、Runtime v8、SDK v3；observations-model.md与observations-implementation-handoff.md仍含实施前FactStore强制Stream/核心推断叙述。03只修改storage-migration、cache compatibility及必要兼容台账；09全链盘点时应将当前实现说明更新，并明确标识仍需保留的历史起点，避免把旧诊断当当前架构。此项交接纳入既有09文档一致性验收，不另建任务。

### 03与现场兼容退出门禁归属

已核对03、04–06验收和observation-storage PRD：03负责通用受支持格式、转换/身份保管、SDK/Runtime矩阵、真实PG迁移及失败恢复的完整实现/自动验收，并记录兼容消费者与退出条件。实际安装Package/contentHash清单和owner确认最长离线/回退窗口是允许移除兼容/发布的现场门禁；在继续保留兼容时，不阻断03通用实现验收。原observation-storage PRD仍ready-for-human承接真实安装/发布，04–06承接各自实际生产者与专有缓存/安装验证，09统一核对。03 done不得解释为现场盘点完成或许可删除兼容；04–06的必要人工缺口仍须保留正确状态。

### 03验收整合与04–06派发

03实现2098ee1a6685a23f821368b3790a11f5e873f484已核验并cherry-pick为main e6564fc296f83df4fa9d32cb91560f3f954cf797，保留两份未提交协调文件。单票最终全仓1428通过、双轴审查无未关闭发现，详见03issue。协调复验：旧接管/迁移与本人/产品20项、SDK59项、Runtime历史缓存10项全部通过（合计89）。日志 /tmp/heartbeat-observations-03-integrated-server.log、/tmp/heartbeat-observations-03-integrated-sdk.log、/tmp/heartbeat-observations-03-integrated-runtime.log。

04/05/06从真实已验收提交e6564fc分别派发，均要求独立工作树、实际生产链路/专有状态恢复、正常单一实现commit、准确区分现场门禁；最多三个活跃实现票已占满。共享Segment SDK可能涉及04/06，要求先报告具体文件/接口由协调安排；各自HTTP测试独立文件。09等待04–06验收，不提前启动。

04–06工作区：04 e487、05 dec3、06 7292，均位于/Users/bytedance/.codex/worktrees/下且基线e6564fc。04先承接共享Segment SDK恢复契约盘点；已通知06避免重复修改，先推进VRChat专有工作并提交接口需求。

共享文件所有权补充：06的VRChat不使用Segment SDK，04独占该组件。06负责CollectorRuntime.ManagedProcess.cs中SDK排空后私有checkpoint仍需防旧包启动/回退的最小通用持久兼容门禁与独立测试；Hub不得引入具名VRChat业务判断，不扩成schema治理。已通知04避免重复修改，有私有状态需求通过此契约交接。

04/06最终共享范围澄清：04实际为System自有NDJSON ingress与InProcess，不直接依赖所谓Segment SDK；当前不改共享SDK/Runtime。06取消新增CollectorDataCompatibility.cs及SDK依赖，VRChat在新checkpoint前原子写通用能力要求marker，Runtime ManagedProcess既有门禁读取；文件契约与端到端启动/回退往返需验证。04的NDJSON新版本保护必须验证旧loader完整错误处理会拒绝并保全，不能只以反序列化抛错宣称回退安全。

05恢复接口所有权：旧Browser已ACK进行中fold缺本地完整快照/版本高水位，Runtime通常仍保留未final的已Delivered记录。现ExternalHost无读口，05获准最小POST /{activationId}/facts/recover（lease、已打开owned stream、准确FactIds）返回完整快照或missing。05负责ExternalHostCollectorProtocolHandler.cs、新CollectorRuntime.ExternalHostRecovery.cs及Browser命名Hub回归；不改SDK DTO。要求复用ready/lease/fence/stream隔离、限批量、深复制、只读，离线/缺失保全旧fold并可重试，不按时间/App猜或扩张停机区间。

04新增共享修复所有权：CollectorRuntime.Protocol.cs中MarkAcknowledgedLiveTraffic及唯一调用处，native空Stream导致SourceLastSeen不更新，已有transcript稳定失败。04按已接受native Fact实际Source修复；拒绝项/缺Source不造来源，旧事实才从Stream回退，重放不冒充live。已通知05避免修改同文件。

Browser Host交叉回归：04 Browser suite出现host.integration两断言失败（旧sink投影空、未ACK保管阻止删除）；05分支同样失败已将Browser.TestHost状态/删除改为Runtime.ReadPendingFacts（含Observation）与完整ConfirmUploadedFacts，并将fixture原生化，5/5通过。04仍需完成e6564fc隔离基线复现，避免仅凭相同断言推定原因。05所有Browser.TestHost修改随05提交，协调合并后重跑跨分支host集成。

04已在独立e6564fc完整git archive中确认Browser host.integration同样2 FAIL/3 PASS，日志/tmp/heartbeat04-browser-baseline.log保留SHA/命令/输出；因此该两项是基线fixture旧投影依赖而非04引入，由05现有修复承接，协调合并后复跑；04不宣称Browser全绿。

### 04/06实现整合与现场状态

06 b8f3f8d整合为8cd2df2，04 225fa4d整合为2feff12，共同FactHttpTests.cs自动合并无冲突。已核对实际生产/专有恢复/共享门禁与审查证据，协调在共同树重建运行HTTP及Runtime专项，暂未登记通过结果。06单票最终全仓1459通过、双轴两P2关闭；04全仓1437通过加修复后的System7项回归覆盖两个旧fixture失败，双轴0发现；04 Browser基线2失败由05承接。两票明确ready-for-human：04真实Windows/macOS权限/回调/安装，06linux-x64真实账号安装及现场兼容窗口等未通过。仅代码与自动验证证据可认可，不得将其整体标done。父PRD/协调记录继续不提交。

04/06协调联合复验通过：真实System/VRChat HTTP10项、共享live Source与持久能力门禁8项，共18项0失败0跳过。日志/tmp/heartbeat-observations-04-06-integrated-http.log、/tmp/heartbeat-observations-04-06-integrated-runtime.log。05仍审查收口中，09尚未启动。

### 05整合及三生产者联合回归

05 77afa243整合为eb4e921，三生产者与前序实现已在main。共同树真实HTTP91/91、Runtime恢复/门禁/live16/16、Browser145/145通过，已关闭04所报Browser host.integration两项基线失败。日志/tmp/heartbeat-observations-producers-integrated-http.log、/tmp/heartbeat-observations-producers-integrated-runtime.log、/tmp/heartbeat-observations-producers-integrated-browser.log。

另发现普通Browser npm test会触发Vite copy-manifest.closeBundle，把旧dist复制回跟踪Package/background.js；已派05修复仅真实build执行并验证，无关代码不改。root规范npm run build后正确制品恢复，不能仅restore掩盖测试副作用。04–06仍ready-for-human，真实平台/账号/Profile安装及兼容退出门禁不冒充通过；其代码和自动验收已整合，可据此开展09不依赖现场的旧主路径/文档/联合代码验收，09的全部前置验收及人工相关项仍须保留未完成，不因进入代码收尾而标04–06 done。

### 09代码收尾启动

Browser测试副作用e1ad18e已整合为2dc5007，root真实build-lifecycle CLI回归1/1通过，跟踪Package不变。09已从真实main2dc5007eb263cb9698b03daea02593b34b67dfcb派发，负责全库旧主路径/兼容消费者盘点、全局文档/30故事映射、最后组合回归与双轴审查。明确是04–06已整合实现之上的可独立代码工作；三票现场仍ready-for-human，09不能提前勾选全部前置验收或全轮done，最终现场缺口按既有owner/发布任务保留。当前仅09活跃实现票，子任务正常commit，协调进度不提交。

09正式任务01a0910d-0d89-7272-8d3f-b777eea85697，工作区8cad，已确认2dc5007干净基线。首个全链盘点发现HeadlessInstancePipelines.Observe直接读取item.Stream.FactKind，native Stream=null使observer异常被捕获，上传成功但CurrentActivity不刷新；09负责公开状态seam RED→GREEN与真实生产后产品/本人/报表组合验证，全局文档并行收口。

### 09代码整合

09正常提交60fe732已整合为main0f11c927f169a4cc4262d48f9066963e692fdc65，二者最终文件树完全一致5c67de8260c6298403cb835499d7f0567f5428ee。修复Headless管理读模型旧Stream/activityKey依赖，退役Input projection/无publisher新写fallback及无消费者接口，保留真实旧cache/receipts恢复，刷新权威文档/ADR/30故事和兼容台账。单票最终全仓1472、Frontend308、Browser146及真实Chrome/System旧loader通过，双轴0未解决。main正在执行dotnet test Heartbeat.slnx --no-restore --nologo，日志/tmp/heartbeat-observations-main-final.log；完成前不登记为全绿。09保持ready-for-human，不代替04–06及原存储现场门禁。

### 最终协调验收

main0f11c92最终重新构建并运行dotnet test Heartbeat.slnx --no-restore --nologo，13项目1472项全通过，0失败0跳过（服务端657、Hub347等）；日志/tmp/heartbeat-observations-main-final.log。Frontend308与Browser146/type/build已核对09最终候选日志，生产代码树与其已验证提交完全一致。父PRD已同步全部代码/自动验收与现场未完成清单，状态ready-for-human；不更改原存储发布/暂停演练状态。无新增纯进度commit。所有活跃编码实现已交付，后续只在用户提供现场证据或新要求时推进相应验收，不反复唤醒完成任务。
