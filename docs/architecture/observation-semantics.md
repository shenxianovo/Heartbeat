# Fact 的观测语义边界

2026-09-11：Observations 01–08 的实现已整合。新 Segment/Event 使用自身 Id、Kind、Collector、FOI、
Aspect、Result、家族时间和 Revision，从第一方生产者经 Runtime 独立保存。09 承接最终组合验收；
04–06 的真实安装/权限/账号验收仍待 owner 完成，业务库迁移及发布仍由原存储 PRD 承接。

## 谁负责解释

- Collector 把原始观测解释为明确的 Aspect 和 Result。
- SDK、Runtime、HTTP、Facts 保管完整快照；同 Revision 的 Aspect 变化也是内容冲突，ACK 必须全等。
- Analytics 只解释其支持的 Aspect 契约，不根据 Collector 名字猜测 Result。新来源使用相同契约即可进入现有分析。
- Dashboard 按 Aspect 选择 Fact View、前台活动及页面关联；未知 Aspect 展示原始结果。
- Source 保留观测来源、显式筛选、ADR-030 深度声明及 Matcher 身份的职责。深度解释仍按声明读字段，不为每个 Collector 新增解析分支。

统一 Facts 不会自动定义计算口径：本次保留原有时长裁剪、设备过滤、分页与重叠处理，不重新定义跨设备或多观察者时长。

## 当前支持的契约

| Aspect | 当前生产者 | Result 与消费 |
| --- | --- | --- |
| `desktop-activity` | System | `activityKey`、可选 `title`、`appIdentityKey`、`attributes`；进入现有前台使用、报表、Recap 主轨及 App 目录查询 |
| `selected-page` | Browser | `activityKey`、`title`、`attributes`；页面视图使用 `url/site/domain`，并行页面以 Collector 和 Result 中的 `windowId` 区分，不要求 Stream |
| `account-location` | VRChat | `activityKey`、`title`、位置字段及 `attributes`；进入活动查询与账号位置视图，不因服务 App 关联而变成设备前台使用 |
| `activity` | 旧通用活动契约及明确采用者 | 非空 `activityKey`、可选 `title/attributes/appIdentityKey`；进入通用活动查询 |
| `input` | System | 既有 `eventType/code/codeSet` 输入契约；仅有效词汇进入输入统计 |
| 其他或历史未知 | 任意 | 原样保存与读取；不解释其中恰巧出现的 `activityKey`、`identityKey`、`eventType` 为已有分析语义 |

Aspect 是 1–128 字符的非空、无首尾空白标识。缺失只服务既有数据兼容。
采用一个已知 Aspect 意味着遵守其结果契约；未知标识不会被拒绝，也不会获得已有契约的含义。
已知活动结果缺少合法 `activityKey` 时仍可存取，但不进入活动分析。
本次仅贯通 Segment/Event；Measurement 输入仍待具体需求。新上传直接携带 CollectorId、FOI 和 Relations；Observer/Target 只在旧 HTTP 与缓存读取边界转换。

## 持久化与升级

- HTTP 严格协议版本为 **5**，先升级 Analytics，再升级上传宿主。
- 第一方 Collector Protocol 要求 **`facts.observation: [2]`**，该能力已包含必需 Aspect，不额外要求旧 `facts.aspect`；未协商成功不交付新 Fact，待发责任继续保管。
- Runtime JSON 为 **v9**，SDK outbox/dead-letter 原生格式为 **v4**。受支持旧文件在读取时转换并保留原版本备份；原身份、Revision、时间、Result、Delivered 与 Gap 保持。进行中旧事实保持 `Kind=null` 和原 Binding/Stream/FactId，沿旧契约终结与准确 ACK。
- ManagedProcess 启动及回退先检查缓存版本与包能力；旧包不得打开新缓存，未知版本停止加载，不产生损坏 Gap。
- System Package **1.2.0** 经实际 AppMonitor/InputEventBuffer、NDJSON ingress **schema 2** 和 Collector Protocol 发布（不使用独立 .NET Segment SDK），原生 Fact 的 Binding 为空；保留的 foreground/input Streams 只承担旧条目与真实 Gap。
- Browser 使用 `browserObservationJournal` **schema 5** 原子保存 fold/outbox、完整 dead-letter 与 Gap，持久扩展 UUID 为 Observer。旧 keys 完整备份并写入读取 fence；缺已 ACK 高水位的旧 fold 通过 `facts.recover` 按准确旧 Stream/FactId 只读恢复，不能猜版本或直接重开。新快照使用持久单调 Revision，EndTime 派生版本只服务旧快照。详见[Browser 专有兼容](../../collection/collectors/Heartbeat.Collector.Browser/cache-compatibility.md)。
- VRChat ManagedProcess 的专有 checkpoint 为 **schema 4**。写入前原子发布通用 `collector-data-requirements.json` 能力要求；启动、候选更新及 LastKnownGood 回退均检查，SDK 排空也不能让旧包读取新专有状态。Hub 不解释 VRChat 文件或业务字段。
- `20260911060000_DirectObservations` 后追加 `20260911070000_IndependentObservationCustody`：新事实保存生产者 Id，自身 Kind 决定家族，旧交付键可空。原生与显式旧适配共用 `SaveSnapshot` 及唯一 Facts；Relations 同事务更新，不由旧 Target 触发器反推。
- Runtime 新事实只有原生上传路径；旧 segment/input 文件仍通过已有上传入口排空，不再提供把新 Fact 转回旧活动模型的运行模式。
- Runtime 实时来源状态按协议已 ACK 的原生 Fact.Source 更新；在线 duplicate/superseded/same-message retry 仍是实时证据，null Source、拒绝、磁盘恢复与 Analytics Confirm 不产生实时盖戳。

预先缺失 Aspect 的旧上传与缓存仍需解释原 `identityKey`/Source。这个解释只存在于兼容边界，
不进入原生保存核心或 Dashboard，也不影响显式 Aspect 的新结果。各格式消费者与退出条件见
[通用缓存接管](observation-cache-compatibility.md)及[兼容台账](compatibility-debt.md)。

## 验证入口

- `FactHttpTests.Aspects`：新 Source 复用已知 Aspect；已知 Source 的未知结果不参与分析；完整 JSON、幂等重放、同 Revision 冲突。
- `InProcessCollectorProtocolTranscriptTests.Aspects`：重启保管与包含 Aspect 的精确 ACK。
- `CollectorProtocolClientTests`：旧缓存迁移、未知版本不改文件、不造 Gap。
- `ManagedProcessCollectorProtocolTranscriptTests`：重启后拒绝旧包打开新缓存；更新/回退仍走同一启动入口。
- `FactHttpTests` 的 SystemObservations、Browser、VRChatCutover：实际生产逻辑经 Runtime、HTTP、隔离 PostgreSQL 与公开读回。
- Browser protocol/storage/background tests：显式 Aspect、单调版本、原子恢复与旧 Hub 能力缺失时不交付；`scripts/smoke-browser-observation-cutover.mjs` 运行临时真实 Chrome Profile。
- `scripts/verify-system-ingress-rollback.py`：实际旧 loader 拒绝新版 ingress，文件集合及 SHA-256 不变。
- Dashboard Fact Views、本人视图与 IndependentAnalysis/Knowledge HTTP：未知 Source/Result、准确关系及派生缓存失效；input 的 Payload/Result 读别名均遵守已有展示边界。
- `dotnet test Heartbeat.slnx --no-restore`；Browser `npm test` 与 `npm run build`；Frontend `npm run verify`；`node scripts/collector-contracts.mjs check`。Browser 普通测试不得改写跟踪 Package，实际 build 才暂存当前源码制品，`build-lifecycle.test.ts` 以完整 CLI 进程验证该边界。

## 对象契约

FOI 引用包含 Kind、Scope、Key，入库后解析为稳定 Object UUID。App 平台身份通过目录解析到跨设备产品；
完整产品合并后，旧产品 Key 的重放沿既有合并回执解析，不复活旧产品。
Fact 关系包含 Kind 与角色成员，继承 Fact 的时间并以 Evidence.factId 绑定准确事实。
同 Revision 改 Collector、FOI、Kind、Aspect、结果、时间或关系均不能冒充幂等。
更高 Revision 可原子更新 Result、结束时间及有依据的关系，Collector/实际 FOI/Kind/Aspect 固定。
App 产品维护沿精确 `AppReferenceEvidence` 解析与修正引用，保留原平台身份依据、Fact Id、Revision、
Result 和时间；维护后原快照重放仍幂等，不是 Collector 换观测对象的纠错通道。

System 观察机器，并明确提供机器/App 关系；Browser 观察 App，提供此次观测的设备关系；
VRChat 观察账号，无设备证据时关系为空。本人关联直接写 `used-by` Relations，引用 Object UUID。
查询返回 FOI 与准确的关系成员；资料表只补展示信息，缺少设备或账号资料不阻止读取事实。
