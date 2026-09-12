# Observations 全链路独立事实契约

Status: ready-for-human

2026-09-11：本轮代码实现、集成与自动验证已完成，全部实现提交至本地 main `0f11c927f169a4cc4262d48f9066963e692fdc65`。
01/02/03/07/08 为 done；04/05/06/09 因真实安装验收缺口保持 ready-for-human。
协调工作区最终重建执行 .NET 全仓 13 项目、1472/1472 通过，0 失败/跳过；最终候选前端308、Browser146及typecheck/build通过。
纯协调记录与父状态按用户要求只修改文件，不单独提交。未推送、部署、操作业务库或恢复暂停的生产演练。

## Problem Statement（重构前，保留原规格）

Heartbeat 要长期保存“谁对什么对象、在什么时间、作出了什么观测”。用户已确认 Observations 模型，
但现有实现仍由旧 Subject/Stream 决定事实的创建条件、家族和上传身份，再附加 Collector、FOI、Aspect 与关系。
即使一条观测语义完整，也必须构造旧交付骨架才能保存。新模型的必需信息却可通过空字段绕过，
正常修订也尚未严格遵守同一事实的身份边界。

仅增加字段或一个独立服务端入口，无法解决 Runtime、生产者、缓存和消费者继续依赖旧模型的问题。
用户要求本轮完成 Observations 在存储、Runtime 及所有相关代码中的实际落地。

## Solution

Collector 产生独立且完整的 Fact；SDK、Runtime 和上传协议保管并交付这个事实，Analytics 保存唯一的事实结果，
查询与 Dashboard 按观测对象、Aspect 和有依据的关系解释它。新事实无需旧 Subject/Stream 即可保存、重传、修订和读取。

沿用现有五表，建立统一观测写入核心。新原生输入严格完整，旧协议及历史缓存经明确的身份与语义转换进入同一核心。
保留旧事实、缓存和交付能力；兼容机制只服务有实际消费者的旧状态。最终所有活跃生产与消费路径使用新契约。

## User Stories

1. 作为数据主人，我希望每条事实明确说明具体 Observer、FOI、Aspect、Result 和 Time，以便多年后仍能理解记录。
2. 作为 Collector 开发者，我希望完整观测可以独立提交，以便接入新来源时无需构造旧 Subject/Stream。
3. 作为 Collector 开发者，我希望自己生成的事实 Id 跨缓存、交付、保存和读取保持稳定，以便准确定位同一事实。
4. 作为 Collector 开发者，我希望每条事实自身声明 Kind，以便传输分组不决定事实家族。
5. 作为数据主人，我希望机器、App、账号和个人可以独立被描述，以便缺少设备或其他对象关系时仍能保管事实。
6. 作为数据主人，我希望未知 Aspect 和未知 Result 字段完整保留，以便未来可以读取和解释已有资料。
7. 作为 Collector 开发者，我希望缺少必需观测信息的新输入被明确拒绝，以便尽早发现契约遗漏。
8. 作为数据主人，我希望旧数据中没有保存过的信息保持未知，以便迁移不会制造历史依据。
9. 作为数据主人，我希望同一 Fact 的 Observer、FOI、Kind、Aspect 固定，以便修订始终描述同一事实。
10. 作为 Collector 开发者，我希望实际更换观测对象或观测含义时创建新事实，以便独立观测不被合并。
11. 作为数据主人，我希望 Segment 持续增长仍保留 Id，以便完整呈现同一段活动。
12. 作为数据主人，我希望合法修订可以缩短 Segment 结束时间，以便最新确认的区间不会被旧快照拉长。
13. 作为 Collector 开发者，我希望 Revision 明确表达快照新旧，以便乱序重放不会覆盖最新状态。
14. 作为 Collector 开发者，我希望同版本相同内容幂等、不同内容明确冲突，以便重试安全且不静默丢弃分歧。
15. 作为数据主人，我希望 Segment 起点与 Event 发生时刻保持稳定，以便事实不会在修订中移到另一个时间。
16. 作为 Runtime 使用者，我希望离线、重启和恢复后仍保管未确认快照，以便网络和进程故障不会丢数据。
17. 作为 Runtime 使用者，我希望迟到 ACK 只确认准确发送过的快照，以便新版本仍能继续上传。
18. 作为 Runtime 使用者，我希望升级保留进行中事实、待发缓存与 Gap，以便升级不切断已有保管责任。
19. 作为 Runtime 使用者，我希望不理解新格式的旧包被明确阻止处理新缓存，以便回退不会造成静默损坏。
20. 作为 Browser 使用者，我希望多窗口各自的事实保持独立且 Revision 可正确增长，以便并行页面和收尾快照不互相覆盖。
21. 作为桌面使用者，我希望 System 活动和输入通过新契约完整运行，以便新模型不是仅用于演示的旁路。
22. 作为 VRChat 使用者，我希望账号事实经重启和离线重放后保持身份，以便缺少设备依据也能准确回放账号活动。
23. 作为历史数据拥有者，我希望旧导入与新重放无论先后都对应同一条已有事实，以便升级不重复计数或丢失旧引用。
24. 作为数据主人，我希望事实和事实绑定关系原子更新，以便读取不会混合不同快照的结果与归属。
25. 作为多设备使用者，我希望同 App 的事实只通过准确关系归属设备，以便设备查询不会串入另一台设备的活动。
26. 作为数据主人，我希望对象、Collector、关系和查询保持 Owner 隔离，以便身份碰撞或引用不能访问其他人的资料。
27. 作为 App 目录维护者，我希望产品合并或平台身份重绑保留观测证据，以便产品维护不丢失事实身份、结果或修订。
28. 作为本人视图使用者，我希望人工使用者关联独立维护，以便确认使用关系不修改原始观测。
29. 作为 Dashboard 使用者，我希望新事实进入适用的查询、分析和回放，以便存储更新后仍能看到完整活动资料。
30. 作为维护者，我希望所有活跃路径和保留兼容都有可判定的验收，以便不会再次把字段贯通误报为模型完成。

## Implementation Decisions

1. **事实契约拥有语义。** 新事实自身包含稳定 Id、Kind、Collector、FOI、Aspect、Result、家族时间和 Revision。
   Owner 取认证身份；关系可为空。来源、声明和展示信息按真实资料保留，核心不靠生成旧 Stream 补齐观测语义。
2. **一个写入核心。** 原生校验和旧输入适配汇入同一观测核心及唯一 Facts 存储。新原生契约与旧状态转换可明确区分，
   不能靠 Relations、FOI 或 Aspect 缺失选择兼容模式。无需为每层再造一套完整事实模型或存储副本。
3. **新旧身份分开承接。** 全新事实使用生产者稳定 UUID；既有事实保留原数据库 Id，按完整旧 Owner/Kind/Stream/FactId
   确定对应。已在进行中或待发的旧事实完成正常终结和确认，转换本身不增加 Revision。不同独立事实碰撞显式拒绝，
   跨 Owner 不覆盖、不泄露记录，不把单个旧 FactId 当成现有数据库主键。
4. **同步解除持久化依赖。** 调整 DTO、实体、旧交付键可空性、唯一约束、外键和读取消费者，
   使新事实不要求 Subject/Stream。继续保留有消费者的交付分组和管理身份，其职责不进入事实身份、FOI 或 Kind。
5. **原生完整，历史如实。** 新事实必需观测信息有效且非空。历史未知通过确定性转换保留；
   旧结果规范化、Source/Aspect 推断仅发生在明确兼容处。数据库支持历史 null 不代表原生输入可以省略。
6. **同一事实不变量。** Observer、实际 FOI、Kind、Aspect 固定，实际变化产生新 Id；正常修订可更新 Result、
   Segment 结束时间和有依据的关系，Segment 起点及 Event 时刻固定。不为采集器填错对象增加业务纠错能力。
7. **修订有序。** 低 Revision 不覆盖高 Revision，同版本比较完整规范语义并处理幂等或冲突。
   新版本替换合法内容，不以最大 EndTime 代替版本。Browser 的新快照使用可持久恢复的单调 Revision，
   不以 EndTime 大小代表新旧；旧缓存转换保留原版本及高水位。
8. **身份解析与版本比较协调。** App 平台身份仍解析为全局产品，目录合并及重绑沿原证据更新引用，
   保持事实 Id、Revision、Result 和时间。重放经当前合法身份映射再比较；已存平台身份依据不得丢失。
   已有历史未知的确定性补全属于兼容转换，不成为新原生事实变更不变量的通道。
9. **关系有依据且原子。** 原生关系绑定准确 Fact、继承其适用时间，随快照在同一事务中更新。
   保持成员角色、种类、基数和 Owner 约束。人工 used-by 关系独立，产品维护与正常快照比较分开处理。
10. **保管覆盖完整链路。** System、Browser、VRChat、SDK、Collector Protocol、Runtime、缓存和 HTTP 均切到新契约。
    保留开始、增长、收尾、重启恢复、离线重试和 Gap 语义；ACK 核对准确发送版本与完整内容，旧响应不确认新快照。
    协议、缓存版本及旧包回退保护同步演进，失败保留可恢复的数据。
11. **消费者完整覆盖。** 原始事实读取、对象筛选、产品维护、本人关联、报表、回放、Recap、Question、Dashboard
    及相关投影均支持无旧 Stream 的新事实。按 FOI/准确关系归属，按 Aspect 选择解释；未知结果完整读取。
    Source 继续保留真实来源、明确筛选及知识声明职责，不替代 Aspect 或 Observer。
12. **迁移有完整性证据。** 使用追加迁移保留历史行、完整 JSON、修订、微秒家族时间、旧引用与关系证据。
    升级失败及重试行为可验证；已发布迁移保持原样。转换不制造新分类、身份命名空间或 Gap。
13. **逐步完成而非旁路完成。** 可分批实现和提交，但只有实际主路径全部切换才达到本 PRD 的完成标准。
    删除无消费者的旧主路径；保留的每项兼容记录实际消费者、退出门槛和验证方式。

## Testing Decisions

沿用已确认的最高可行测试 Interface：真实 HTTP 写入与公开查询；第一方 Collector 发布经 Runtime、HTTP、
真实 PostgreSQL 到读取及 ACK。优先扩展既有 fixture，不新增仅供测试调用的旁路。缓存文件和数据库迁移
通过各自真实升级/重启入口验证；必要的 Browser 快照版本测试只断言可观察的发布与恢复结果。

先写能稳定失败的最小行为测试，再修复根因。避免测试私有方法调用顺序或重复断言字段赋值；
故障用例断言持久结果、待发状态、响应及重新启动后的状态。

| 验证主题 | 必须观察到的结果 | 既有测试先例 |
| --- | --- | --- |
| 独立原生契约 | 无旧 Subject/Stream、无关系的 Segment/Event 保存、读取、重传及修订成功；实际保存生产者 Id | FactHttpTests、FactStoreTests |
| 完整性和身份 | 缺 Collector/FOI/Aspect 被拒绝；固定字段不能借高版本替换；Owner 与独立 Id 碰撞安全处理 | FactHttpTests.Foi、ObservationStorageTests |
| 快照顺序 | 同版本幂等/冲突、乱序、增长/缩短、固定起点/事件时刻正确；事务失败不留下部分写入 | FactStoreTests、FactHttpTests.Cutover |
| 第一方实际链路 | System 活动和输入、Browser 多窗口、VRChat 账号都经新契约写入并可读取 | FactHttpTests.Observations、FactHttpTests.LongSession |
| 重启与 ACK | 旧 ACK 到达时新快照仍待发；恢复后身份/版本不倒退；Gap 保管不丢失 | InProcessCollectorProtocolTranscriptTests、CollectorDeliveryOwnershipTests |
| 缓存升级及回退 | 受支持旧缓存及进行中事实完整恢复，旧包不能处理新缓存；失败留存原状态与恢复依据 | CollectorProtocolClientTests、ManagedProcessCollectorProtocolTranscriptTests、Browser 缓存测试 |
| Browser 版本 | EndTime 相同但发布内容变化、合法缩短及重启后发布都获得更新版本；旧队列重放不倒退 | Browser protocol/delivery/fold 测试 |
| 历史对应与迁移 | 旧导入先到/重放先到/旧缓存迟到均保持唯一事实及原行 Id；重复升级不改内容，冲突不丢行 | FactMigrationTests、ObservationFactsMigrationTests、DirectObservationMigrationTests |
| 对象与关系 | 同 App 多设备准确归属，关系时间跟随修订，人工关联独立，跨 Owner 和错误角色拒绝 | ObservationStorageTests、本人关联与 App 目录测试 |
| 读取与解释 | 无 Stream 新事实进入适用分析，未知 Aspect 不被猜义；产品纠错后重放仍正确 | FactHttpTests.Aspects、ObservationDepthTests、Frontend Fact Views 测试 |

本轮代码与自动验证验收（以下勾选不替代后列现场验收）：

- [x] 无旧 Subject/Stream 的新 Segment/Event 通过真实入口完成保存、重传、修订和读取。
- [x] 原生必填、同 Fact 固定字段、Revision 与关系原子性在 Runtime 和存储一致执行。
- [x] DTO、实体、数据库及所有活跃读写消费者解除旧交付键的强制依赖。
- [x] System、Browser、VRChat、SDK、协议、Runtime 和缓存实际切换，含 Browser 单调 Revision。
- [x] 离线、重启、进行中事实、迟到 ACK、Gap、旧缓存及包回退保管通过。
- [x] 旧身份接管、历史未知、完整内容、追加迁移与失败重试保全通过。
- [x] 查询、分析、Dashboard、本人关联、产品维护及准确设备归属通过。
- [x] 相关回归、仓库规定检查、规范与规格审查完成；全库残余依赖盘点无未承接主路径。
- [x] 所有保留兼容有消费者与退出条件，文档/任务状态同步；人工验证缺口有步骤、负责人和状态。

测试执行以仓库当前入口为准。平台或真实安装无法自动验证时明确记录缺口，不以其他平台的通过代替；
如果本轮实现仍依赖人工门禁，状态转 ready-for-human，不能提前标 done。

## 仍待现场验收

- [ ] 04：owner / Desktop维护者完成真实 Windows、macOS 安装、权限、窗口/输入回调及离线重启验收，步骤见 System README 与04 issue。
- [ ] 05：owner / Browser维护者完成实际 Chrome/Edge Profile 升级和旧状态验收；临时真实 Chrome运行已经通过，但不代表全部实际 Profile。
- [ ] 06：owner / Headless维护者完成真实 linux-x64 安装、逐VRChat账号授权及恢复验证；当前自动链路使用隔离mock账号。
- [ ] owner / Collection与Analytics维护者确认实际Package/contentHash、数据目录schema、待发/未终结/隔离清单及最长离线/回退窗口。此前保留必要兼容，不能据自动通过删除。

09的[30故事、组件契约和现场承接矩阵](issues/09-contract-and-integrated-verification.md)记录完整证据与步骤。
业务库迁移、完整生产副本资源/恢复演练仍属于原 observation-storage 发布任务，保持原状态，不计为本轮已执行。
本人真实UI smoke本轮未使用实际开发凭据执行；本人HTTP组合和Dashboard组件已验证，不宣称真实UI安装验收完成。

## Out of Scope

- Measurement 的新生产业务与具体数值契约、DataSource、任意对象关系推理。
- 重新定义时长统计、知识模型、Source 深度声明或既有产品目录语义。
- Fact 撤回、多版本历史结果档案、采集器对象填写纠错及 Schema 格式治理。
- 仅为凑表数删除管理/产品资料，或机械删除仍有实际消费者的兼容字段与历史迁移。
- 业务库迁移、部署、恢复已暂停的完整副本资源/恢复演练；这些继续由原存储 PRD 承接。

## Further Notes

- 模型权威：[Observations 与 Facts](../../docs/architecture/observations-model.md)、[ADR-059](../../docs/adr/059-observation-storage-five-tables.md)。
- 旧数据保全及退出：[迁移映射](../../docs/architecture/observation-storage-migration.md)、[兼容台账](../../docs/architecture/compatibility-debt.md)。
- 静态证据索引：[实施交接](../../docs/architecture/observations-implementation-handoff.md)；其中路径用于定位，不替代本规格的验收。
- 外部上线门禁：[五表存储 PRD](../observation-storage/PRD.md)，保持 ready-for-human，不因本规格重开而复用旧通过结论。
- 本轮已确认的独立身份契约取代旧原生写入中由 Stream 限定身份的目标；旧契约仅服务仍需保管的历史状态。
  下一步在本 PRD 下拆出有阻塞依赖、可独立验收的实施任务；拆分不缩减全链路完成标准。

## Comments

### 2026-09-11 最终代码整合与状态收口

- 09提交60fe732整合为本地main0f11c92，两者文件树完全一致；全局旧路径盘点、Headless原生状态修复、Input旧新写fallback清理、权威文档/兼容台账/30故事映射完成。
- 协调工作区实际执行 `dotnet test Heartbeat.slnx --no-restore --nologo`，13项目1472/1472通过、0失败/跳过；日志 `/tmp/heartbeat-observations-main-final.log`。最终候选前端308、Browser146及type/build、真实临时Chrome和System旧loader验证证据见09。
- 双轴审查最终无未解决发现；代码与自动验证完成，现场缺口如上，父PRD改为ready-for-human而非done。04–06/09现场状态保留，未操作原发布门禁。
- 当前保留的旧HTTP/缓存适配、进行中旧身份、payload读取别名和旧Target列/index有明确兼容/历史schema边界；它们不再决定新事实语义。物理删除旧列仍需追加迁移及原存储上线/恢复验收。

### 2026-09-11 最后代码收尾

Browser构建测试副作用已修复并复验，main2dc5007包含前八票全部实现。09从该提交启动全库残余依赖清理、文档与30故事映射、联合自动验证；04–06现场状态仍ready-for-human，代码收尾不代表全部前置现场验收完成。

### 2026-09-11 Browser整合与三生产者共同验证

- 05整合为eb4e921，共同树HTTP91、Runtime16、Browser145全部通过，Browser TestHost两项基线失败已关闭。
- 测试触发Vite旧dist覆盖跟踪Package的副作用已交05修复，root规范build恢复当前制品。
- 04–06代码/自动验证已具备09代码收尾基础，但真实现场门禁仍未完成；09可开展独立代码清理/文档/组合验证，不能提前宣称全轮或所有前置验收完成。

### 2026-09-11 System/VRChat实现整合

- 06整合为8cd2df2、04整合为2feff12，协调共同树重建通过18项真实HTTP/Runtime边界回归。
- 两票实现和自动验证完成，均保留ready-for-human：System真实Windows/macOS安装权限/回调、VRChat真实linux-x64账号安装与现场兼容清单/窗口尚未通过；不以隔离fixture替代。
- 05仍在审查收口，将修复已确认Browser TestHost基线失败；09尚未实施。

### 2026-09-11 Ticket03验收与生产者迁移

- 03历史身份/通用缓存/迁移实现已验收并整合为e6564fc，最终全仓1428通过，双轴审查无未关闭发现。
- 协调工作区重建复验89项接管/迁移/本人/产品/SDK/Runtime专项全部通过，工作区纯协调记录未提交。
- 04、05、06分别迁移System、Browser、VRChat真实生产路径和专有持久状态。03通用fixture不替代这些验收，也不代表现场兼容退出/发布门禁完成。

### 2026-09-11 实现提交与后续工作树

用户明确只禁止定时纯进度提交，实际实现必须正常提交。02/07/08共同已验证文件树以da6dd3f提交；父PRD和协调记录继续保留未提交。03已获提交基线与保留增量切换指令，后续按正常实现提交与集成流程推进。

### 2026-09-11 Ticket 02 验收与03派发

- 02最终实现以文件补丁整合，无新增commit；其全仓1385项与双轴审查通过。
- 协调工作区重建复验服务端116项、Hub322项、SDK51项全部通过，02 issue done。
- 03从已验收文件快照派发，继续旧事实、缓存、迁移与进行中事实接管；专有生产者04–06仍等待03，不把通用原生链路视为全体切换完成。

### 2026-09-11 Ticket 07/08 验收整合

- 对象与产品维护、Person视图、分析与Dashboard两票已验收，来源提交24e968a/adcec27以不提交方式整合并取消暂存。
- 最终client基于联合OpenAPI生成；共同工作区后端24项专项、前端308项及typecheck/build通过。
- 实现已在用户纠正提交边界后合并提交为da6dd3f；后续实现正常提交，仅纯协调进度不提交。02已验收并通过联合回归，03及下游待验收，整体验收09尚未开始。

### 2026-09-11 Ticket 01 验收整合

- 单个实现提交 `bcb475c` 整合为本地 main 的 `6976e10`，01 issue 全部验收完成。
- 编码会话完成全仓1367项测试与最终改动相关回归，双轴审查关闭；详细边界与命令见01 issue。
- 协调会话整合后重新构建并执行独立观测/关系/迁移专项，34/34通过；未提交的协调文档完整保留。
- 已启动02、07、08；其余验收继续由对应任务承接，不声明Runtime、生产者或全部分析已完成。

### 2026-09-11 本轮规格收口

- 用户确认同 Fact 的 Observer/FOI/Kind/Aspect 固定，并保留 Revision 处理增长、乱序与精确 ACK。
- 用户确认本轮全部完成 Observations 新模型，包括存储、Runtime 及所有相关代码。
- 复核发现旧必填交付键、隐式兼容分流、原生完整性缺口及 Browser EndTime 派生版本等问题，
  因此重开原 PRD 为 ready-for-agent；上轮 done 仅保留为此前范围的历史记录。
- 本轮仅生成规格并复核文档；尚未修改业务代码、执行运行测试或拆分实施任务。

### 2026-09-11 上轮对象字段收敛记录（历史）

上轮 Status: done；基线 `05e892a`。当时范围为收敛采集、上传、查询中的 FOI/Relations。
下列勾选及测试结果属于上轮，不代表本轮独立契约验收通过。

#### 当时契约

- Fact 新输入直接携带 CollectorId、Foi(Kind/Scope/Key)、Relations、Aspect、Payload 和家族时间。
- 对象引用可离线产生：machine/heartbeat.device/既有设备引用；app/heartbeat.app-identity/平台身份由目录解析成全局产品；app/heartbeat.app/产品Key；account/服务/账号；person/heartbeat.person/既有Reference。
- 关系随事实提供种类和角色成员，作用域为该 Fact 的时间，Evidence 绑定准确 Fact；无证据则空列表。
- Source/Stream/Subject 保留交付、配置和知识声明职责，不决定新 Fact 的对象。
- System 保持机器 FOI，前台关系明确引用机器/App；Browser 直接是 App FOI 和 observed-on；VRChat 直接是账号，不补设备。
- Query/Dashboard 以对象 UUID 和关系读取/分组；旧数字ID只用于产品资料/过滤，不是FOI身份。
- 旧HTTP/缓存兼容入口仍支持已确认安装窗口，新链路不生成Target/application-context。历史迁移不改写，身份/Revision/时间/Result/ACK不变，不造分类或Gap。
- 不新增没有业务生产者的 Measurement 契约，不改时长口径，不执行业务库迁移、部署及暂停的资源演练。

#### 当时验收

- [x] 三个第一方 Collector 直接发布 Collector/FOI/Relations，跨平台产品解析不造两个App。
- [x] SDK/Runtime持久缓存迁移、协商、精确ACK与旧包回退保护完整。
- [x] 原生摄入直接保管FOI/关系，旧触发器不能覆盖；同修订冲突与Owner隔离成立。
- [x] Experience/活动/本人查询与Dashboard按FOI/Relations运行，账号缺设备不影响事实。
- [x] 旧缓存重放、产品纠错、本人关联修改与历史数据库迁移保全通过。
- [x] 相关/全仓验证、规范及规格审查、文档和兼容退出收口完成。

#### 当时实施与验证记录

- 已替换原生采集/传输、数据库写入、查询与 Dashboard 对象契约；删除应用上下文实体、本人关联旧表与旧反推触发器。
- 生产已无消费者的 Runtime 旧投影模式及专属测试一起退休；实际旧缓存仍在入口迁移，不另造 Gap。
- 基线 `05e892a` 的隔离 worktree 上，新 FOI/Relations HTTP 测试因缺失 FOI 响应按预期失败；临时 worktree 已删除。
- 数据库专项 27 项通过；DirectObservations 迁移保留 Facts 全字段与关系 UUID/Evidence，重复升级及 EF 模型检查通过。
- 演练脚本 SQL 已在隔离 PostgreSQL 18 执行逐行/对象归属/聚合及分页对照通过，未恢复暂停的完整副本演练。
- Browser 113 测试与构建通过；Frontend 291 测试、类型检查、构建与 NSwag 生成通过。
- 最终 .NET 1336 项通过（全仓运行及失败项目修复后局部复验）；共享 HTTP fixture 合并后长会话再验 1 项通过，命名检查和 diff 检查通过。
- 按用户要求先完成代码再集中回归，删除旧投影专属测试并合并重复 fixture；手写代码净删 459 行，含生成文件的整体 diff 也为净删除。
- 部署、真实安装与业务库资源/恢复门禁继续由 observation-storage PRD 承接，本任务不声明完成这些门禁。
