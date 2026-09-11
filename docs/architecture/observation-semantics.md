# Fact 的观测语义边界

2026-09-11：在五表存储基线 `90273f9` 上实现。Collector 对 FOI 产生 Fact；
Fact 包含 Aspect、Result、Time。业务库尚未迁移，部署/真实安装验收继续由原存储 PRD 承接。

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
| `selected-page` | Browser | `activityKey`、`title`、`attributes`；页面视图使用 `url/site/domain`，并行页面使用 Stream 内 `windowId` |
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
- 第一方 Collector Protocol 要求 **`facts.aspect: [1]`** 与 **`facts.observation: [1]`**；未协商成功不发送新 Fact，也不消费待发缓存。
- Runtime JSON 为 **v8**，SDK outbox/dead-letter 原生格式为 **v3**。旧文件在读取时迁移并保留原版本备份；FactId、Revision、时间、Result、Delivered 与 Gap 保持。
- ManagedProcess 启动及回退先检查缓存版本与包能力；旧包不得打开新缓存，未知版本停止加载，不产生损坏 Gap。
- Browser 使用 `pendingObservationFacts` / `browserObservationDeadLetters`；旧 key 仅在读取时转换，成功持久化后删除。新旧队列并存按 FactId/Revision 合并，同修订不同内容拒绝覆盖。
- 当前候选 migration 为 `20260911060000_DirectObservations`。Fact 的 Collector/FOI/Aspect 直接写入；Relations 在同一事务内更新，不再由旧 Target 触发器反推。
- Runtime 新事实只有原生上传路径；旧 segment/input 文件仍通过已有上传入口排空，不再提供把新 Fact 转回旧活动模型的运行模式。

预先缺失 Aspect 的旧上传与缓存仍需解释原 `identityKey`/Source。这个解释只存在于兼容边界，
不进入 Dashboard，也不影响显式 Aspect 的新结果。移除门槛见[兼容台账](compatibility-debt.md)。

## 验证入口

- `FactHttpTests.Aspects`：新 Source 复用已知 Aspect；已知 Source 的未知结果不参与分析；完整 JSON、幂等重放、同 Revision 冲突。
- `InProcessCollectorProtocolTranscriptTests.Aspects`：重启保管与包含 Aspect 的精确 ACK。
- `CollectorProtocolClientTests`：旧缓存迁移、未知版本不改文件、不造 Gap。
- `ManagedProcessCollectorProtocolTranscriptTests`：重启后拒绝旧包打开新缓存；更新/回退仍走同一启动入口。
- Browser protocol tests：显式 Aspect 与旧 Hub 能力缺失时不交付；Dashboard Fact Views：语义与 Source 正交。
- `dotnet test Heartbeat.slnx --no-restore`；Browser `npm test`；Frontend `npm run verify`。

## 对象契约

FOI 引用包含 Kind、Scope、Key，入库后解析为稳定 Object UUID。App 平台身份通过目录解析到跨设备产品；
完整产品合并后，旧产品 Key 的重放沿既有合并回执解析，不复活旧产品。
Fact 关系包含 Kind 与角色成员，继承 Fact 的时间并以 Evidence.factId 绑定准确事实。
同 Revision 改 Collector、FOI 或关系也属于冲突；更高 Revision 原子更新事实和关系。

System 观察机器，并明确提供机器/App 关系；Browser 观察 App，提供此次观测的设备关系；
VRChat 观察账号，无设备证据时关系为空。本人关联直接写 `used-by` Relations，引用 Object UUID。
查询返回 FOI 与准确的关系成员；资料表只补展示信息，缺少设备或账号资料不阻止读取事实。
