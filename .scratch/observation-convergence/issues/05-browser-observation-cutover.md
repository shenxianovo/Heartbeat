# 05 — Browser 多窗口全链路切换

**What to build:** 真实 Browser 多窗口活动通过新观测契约发布、持久保管和读取；快照使用可恢复的单调 Revision，旧扩展缓存升级、Service Worker 重启和迟到 ACK 均保持独立事实。

**Blocked by:** [03 — 旧事实与旧缓存无损接管](03-legacy-fact-and-cache-takeover.md).

Status: ready-for-human

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [x] 实际 Browser fold/发布/交付经 ExternalHost、Runtime、HTTP 到 PostgreSQL 与读取使用新契约，不在适配处虚构旧 Subject/Stream 使新事实成立。
- [x] 持久扩展安装身份继续表示具体 Observer；FOI 为 App 产品，设备关联来自明确 observed-on 证据，不以 Runtime 实例或宿主猜测归属。
- [x] 多窗口同时访问相同 App/页面仍保持各自 Fact Id；窗口转场、关闭、重开及活跃快照增长遵守现有活动连续性规则，保留必要窗口证据但不将窗口登记为新的持久 FOI。
- [x] 新快照维护持久化单调 Revision，EndTime 相同而完整发布内容变化、合法结束时间缩短及收尾都可得到更高版本；重传同一快照保持原版本，不以 EndTime 计算新旧。
- [x] 旧 endTime 派生版本及旧 pending/fold/dead-letter 状态迁移保留原 Id、版本高水位、完整内容和待发责任；转换本身不增加版本，新变化的版本高于已保管旧版本。
- [x] 新旧队列并存、Service Worker 重启、离线重试与升级恢复不重复产生事实；同版本冲突保留可恢复依据，不能静默覆盖其中一份。
- [x] 迟到 ACK 对应准确发送版本与完整快照，不能删除已增长或更新的当前快照；Gap 和包/协议能力保护在新链路仍正确。
- [x] 用真实 Browser 生产逻辑贯通发布与服务端读回，并以 UI/运行 fixture 验证实际多窗口行为；仅手写原生 HTTP 数据不能完成本项。
- [ ] 对应 Browser、ExternalHost/Runtime、缓存和 HTTP 回归通过，明确真实 Profile/浏览器验收步骤、证据或待承接状态；不恢复暂停的生产部署。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。

### 2026-09-11 实现与真实生产链路

工作区 `/Users/bytedance/.codex/worktrees/dec3/Heartbeat`，分支 `codex/observation-ticket-05`；
初始化确认 HEAD 与 main 均为 `e6564fc296f83df4fa9d32cb91560f3f954cf797`，没有使用旧临时补丁。
本票只维护自己的 issue、Browser 生产与专有证据，以及协调者批准的最小 ExternalHost 恢复读口。
父 PRD、ORCHESTRATION、其他 issue 由协调任务维护。

- 真实 fold/Segment SDK 生成新 `kind=segment`、UUID 与单调 Revision；同快照重传不增版，
  同 EndTime 内容/终态变化与合法缩短分别增版，lastSnapshot 随 fold/outbox 原子持久保管。
- Browser Package 和 hello 协商 `facts.observation:2`；原生 Fact 自带 Kind/Source，发送时
  没有旧 Stream。CollectorId 是扩展持久安装 UUID，FOI 是 App 产品，初始化中明确 Machine
  资料形成 observed-on；windowId 只在 Result 中区分并行事实。
- 旧进行中/待发仍无 Kind，保持原 Id、Stream 对应及旧 endTime 高水位；四代真实 local/session
  keys、未知 Result、dead-letter 冲突依据和 Gap 经 schema5 journal 转换保全。转换自身不加版本。
- 旧 Chrome 没有 delivered key，已 ACK 后可能没有本地高水位。新增受 Ready/lease/writer/fence
  约束的 `facts.recover`，只读准确旧 Stream/FactId 完整快照；缺失、离线、跨 Host 或撤销不确认。
  恢复后在已知 EndTime 准确 final，再从当前窗口观测新开；不把停机时间扩张进旧事实。
- 新 journal 与旧 keys 原始备份、两类旧队列读取 fence 同次保存；失败保留原件，重试不换 Id。
  固定历史 `e6564fc` loader 实际执行时拒绝新状态且不擦写；未盘点其他旧包不能据此宣称通过。
  异常安装 UUID 被明确拒绝并保留，不悄悄重新分配 Observer。
- 同版冲突保留原队列及完整诊断快照；ACK 比较完整语义（含未知字段），迟到响应不移除更新。
  完整浏览器重启按最后已知时刻收尾；新建但尚未 flush 只记录已知起点的零长度 final。
  停用保留待恢复旧 fold，不凭空清除恢复责任。

### RED → GREEN 与验收入口

稳定 RED 包括：SDK 缺单调 Revision、原生仍带旧 Stream、同版覆盖丢失冲突证据、未知 Result
发送/后续修订丢字段、恢复归属本身误增版本、错误窗口恢复、原子关闭写失败、旧队列非法值
被当空队列、整浏览器未 flush 状态扩张停机区间、停用删除待恢复 fold、非法安装 UUID，以及
Runtime 无精确恢复路由（404）。每项均经对应公开接口或真实存储/事件入口修复并回归。

- `FactHttpTests.Browser.cs` 使用 Node 执行真实 Browser fold/SDK/protocol，并经真实 ExternalHost
  Kestrel、Runtime 持久重启、HeartbeatApiClient、隔离 PostgreSQL 和公开查询；包含多窗口、同
  EndTime 改标题、缩短 final、重开、迟到 ACK、Gap、设备筛选、已 ACK 未终结重启，以及真实旧
  Chrome keys 无高水位→恢复→旧 HTTP→原数据库行不变与完整 JSON。独立定向 1/1 通过。
  此 HTTP fixture 在生产改动后接线，未将测试接线错误冒充基线 RED。
- `BrowserExternalHostRecoveryTests` 8/8 与既有 ExternalHost 合计 46/46 通过；完整已 ACK 旧
  快照/已 final/missing、批量上限、wrong lease、跨 Host、未 Ready、被替换、过期、Runtime
  撤销均覆盖，读前后 journal 不变。
- `scripts/smoke-browser-observation-cutover.mjs` 在新建临时真实 Chrome Profile 中执行 MV3：
  两窗口同页、关闭重开、Service Worker stop/start、Runtime 重启、离线 pending、Chrome 与
  Host 完整进程重启及 UUID/Observer 重放通过。该脚本没有真实账号，development capability
  绑定只连接本 fixture；HTTP/PostgreSQL 由上项同生产逻辑的集成继续验证。
- 原有 Browser host.integration 的投影为空与拒绝删除两条在 `e6564fc` 独立基线也失败
  （协调04提供 `/tmp/heartbeat04-browser-baseline.log`）。TestHost 改从 Runtime 真实保管读取
  Observation，并完整 ConfirmUploadedFacts；不再以旧 sink 当新观测权威，修复后 5/5 通过。

### 安装门禁与 09 交接

真实 Chrome/Edge owner Profile 安装升级仍由 owner 承接，步骤与支持格式/兼容消费者/退出条件见
[Browser 缓存与安装验收](../../../collection/collectors/Heartbeat.Collector.Browser/cache-compatibility.md)。
本票自动 fixture 及临时 Chrome 运行不等于实际所有 Profile 安装通过，因此状态保留
`ready-for-human`，最后验收框不提前勾选。实际 Package/contentHash 全量清单、最长离线和回退
窗口继续归原 observation-storage 发布门禁；不虚构完成、不删除兼容。

09 统一更新全局模型/语义文档的旧版本和起点叙述，并在兼容台账引用本票 Browser schema5
journal、旧 keys 读取 fence 与 facts.recover 消费者。现有通用 SDK schema4、Runtime schema9
未在本票改版。共享恢复口由协调明确分配；未改04拥有的 Runtime.Protocol/ActivationSession。
没有部署、推送、业务库变更、真实账号操作或恢复暂停的完整生产副本/资源演练。

### 全仓验证与审查修复

- `dotnet tool restore`、`dotnet restore Heartbeat.slnx`、`dotnet build Heartbeat.slnx --no-restore`
  成功；`dotnet test Heartbeat.slnx --no-build --nologo` **1437/1437**（13 项目，Server 648，
  Hub 340，0 失败、0 跳过）。`dotnet format style ... --diagnostics IDE1006 --verify-no-changes`
  与 `git diff --check` 通过；.NET 全仓日志 `/tmp/heartbeat05-dotnet-all-final.log`。
- Browser 审查修复后 `npm run build`、全套 **145/145** 与
  `node scripts/collector-contracts.mjs check` 通过。日志分别为
  `/tmp/heartbeat05-browser-build-reviewed.log`、`heartbeat05-browser-all-reviewed.log`、
  `heartbeat05-contracts-reviewed.log`。定向验证均与全套重叠，不累计数字。
- 双轴初审固定 `e6564fc` → `b56f435aea64a869bfa7f76a8b96344d80fd66ca`；无本票进度 commit，
  用完整固定 tree 包含所有新文件接受审查，最终只提交通过验收的实现。
- Standards 初审发现 P2：普通 ACK 无条件截断超过100条的未解决 dead-letter，丢恢复证据。
  101条完整条目正常 ACK 的稳定 RED 后删除静默截断；GREEN 保留全部记录。
- Spec 初审另发现 P1：满5000条队列时新活动 checkpoint 失败阻断 deliveryCycle，永久无法排空。
  真实 background alarm 先 RED（fetch=0），修复后离线仍尝试交付；联网后队列4500，下一轮原
  未入队活动同 Id 进入并实际 ACK。一处临时 storage.set 失败也不推进 fold，重试同 Id/Revision。
  延伸检查完整浏览器启动的同类阻塞也先 RED；现在安装重试 alarm、允许旧队列排空，再完成
  原已知时刻 final，之后才对当前窗口重开，开发 reload 不能绕过未完成的恢复。
- RED/GREEN 日志：`/tmp/heartbeat05-deadletter-review-red.log`、
  `heartbeat05-full-queue-review-red.log`、`heartbeat05-backpressure-green.log`、
  `heartbeat05-startup-pressure-red.log`、`heartbeat05-startup-pressure-green.log`。

Friction closeout：旧endTime修订、fold/outbox分写、无已ACK高水位、旧loader无版本guard、
满队列不能交付及诊断截断分别在本票修复并记录真实消费者/退出条件；局部Browser README/SDK
说明同步。全局架构叙述与兼容台账交09统一更新，真实Profile/窗口与发布门禁明确由owner承接。

### 最终运行证据

审查修复后的真实 HTTP（含旧 fold 无高水位恢复、完整 JSON、准确旧数据库行）再次 **1/1 PASS**，
日志 `/tmp/heartbeat05-http-reviewed.log`。最终真实 Chrome script 再次通过，日志
`/tmp/heartbeat05-chrome-final.log`，运行完成于 `2026-09-11T15:08:54.042Z`；完整报告
`/var/folders/ns/7njj05zs4x70mm21q4fdznbw0000gn/T/heartbeat-browser-cutover-C7370i/report.json`。
以下是该一次性临时 Profile 的制品/身份证据，**不是 owner 实际安装全量清单**：

- Package `heartbeat.collector.browser@0.1.0`，content hash
  `sha256:af95126867aa24bfe0074a23e3f99cb79138f3d45c01172af7f7cd582420a70c`。
- Artifact `browser.extension`，hash
  `sha256:50407f10d74a6fcc84632c56d1041ea74bf7049931eaa566d8fcca47868491b6`。
- 扩展 Observer `1485e6a6-4550-4611-8955-d5c19aa6f634`，FOI 平台依据 `mac:com.google.chrome`。
  两窗口事实 `01a09103-6fc1-7d22-8acb-4af82330bf0b`、
  `01a09103-6fc3-7390-ac81-6d5e8cecebe8`；离线重放事实
  `01a09103-a060-7b6f-8827-95e7de34771a`。
- 报告确认 `productionProfileUsed=false`；twoActualWindows、closeAndReopen、
  serviceWorkerRestart、runtimeRestart、browserAndHostProcessRestart、offlineReplay 均为 true。

## Standards

固定基线 `e6564fc` 到最终实现树 `4ca343a2bac2c269842dd171f8b3d2414cd3ad5f`，独立复核无未解决
发现。初审 P2（普通 ACK 截断恢复证据）已以101条回归关闭；后续背压与启动恢复符合持久责任，
未发现新增硬标准或维护性 heuristic 问题。固定树之后只追加本票最终运行/审查证据。

## Spec

独立初审 P1（满队列阻断上传）及 P2（ACK 丢冲突证据）均已修复并由原审查者复核关闭。
复核者独立运行 background/delivery **27/27 PASS**，确认原活动同 Id 实际得到 ACK、完整浏览器
重启按已知时间收尾、初始化 guard 不绕过恢复；无新增缺失、partial 或 scope creep。
真实 Profile 安装与外部发布条件仍由上述人工门禁承接。

审查合计：Standards 1 已关闭、0 未解决；Spec 2 已关闭、0 未解决；两轴均无剩余最高风险项。
实现完成、自动验证完成，真实安装人工验收尚未完成，保留 `ready-for-human`。

### 2026-09-11 协调合并后补充：测试收尾不得改写 Browser Package

协调在 main 合并04/05/06后发现普通 `npm test` 会把陈旧 dist 复制进跟踪的
`Package/browser-extension`。本票工作区以 `77afa243` 为增量基线复现并修复，不操作协调工作区：
Vite `copy-manifest` 插件没有限定命令，Vitest 的收尾也执行 `closeBundle`。

新增 `tests/build-lifecycle.test.ts` 用复制的真实生产 Vite 配置，在临时项目放入陈旧 dist 和不同的
Package，启动完整 Vitest CLI 并等待进程退出。稳定 RED 精确观察到 Package 从
`previously-staged-package` 被覆盖为 `stale-dist`，且多写 manifest。为插件加 `apply: 'build'`
后同一测试 GREEN；随后真实 Vite build 将当前源码与 manifest 正确暂存，整个 Package 与 dist
逐文件一致。测试不是只断言配置值，也不需要改写本工作区的真实 dist 来模拟故障。

`npm run build`、Browser 全套 **146/146**、contracts 与 `git diff --check` 通过；在本工作区运行
完整普通 `npm test` 前后对 Package 五个文件做 SHA-256 比较，路径和字节全部不变。
现有跟踪 Package 无 diff；本补充只有配置、必要回归和本票证据，不靠 git restore 隐藏副作用。
日志 `/tmp/heartbeat05-build-lifecycle-red.log`、`heartbeat05-build-lifecycle-green.log`、
`heartbeat05-build-lifecycle-build.log`、`heartbeat05-build-lifecycle-all.log`。修复不变更生产观测
协议、Runtime 或原人工安装门禁，状态继续 `ready-for-human`。

补充修复的固定增量审查 `77afa243` → `9b3b4c8c2e6377897bf1c77d8fdaf46391a35fa2`：
Standards 与 Spec 均0发现；Spec 原审查者独立重跑 CLI 生命周期回归1/1通过。
