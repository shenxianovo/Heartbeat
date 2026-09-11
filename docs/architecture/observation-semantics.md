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
本次仅贯通 Segment/Event；Measurement 输入仍待具体需求。上传的 Observer/Target 继续转换为 Collector/FOI，未引入另一套 FOI API。

## 持久化与升级

- HTTP 严格协议版本由 3 升至 **4**，先升级 Analytics，再升级上传宿主。
- Collector Protocol 增加 **`facts.aspect: [1]`**。第一方包声明并要求该能力；SDK/Browser 在未协商成功时不发送带 Aspect 的 Fact，也不消费待发缓存。
- Runtime JSON 当前为 **v7**。v1–v6 按既有顺序迁移，保留原版本备份；只对缺 Aspect 的旧记录从原契约恢复语义，重启不再改写含义。
- SDK outbox/dead-letter **实际含 Aspect 时写 v2**；无 Aspect 的兼容内容仍写 v1。第一次从 v1 写入 v2 时保留 `.v1.bak`。备份不是当前待发数据的替代品。
- Runtime 启动 ManagedProcess（包括自动回退）前检查 SDK 的 `collector-protocol-outbox.json` 与 `collector-protocol-dead-letter.json`：v2 需要包声明 `facts.aspect` v1，未来版本停止启动。旧 SDK 不得打开这些新文件；不兼容时保留文件并报告启动失败，不产生 Gap。清空所有带 Aspect 的待发/死信后，v1 文件可以被旧包读取。
- 当前 SDK 先检查 envelope 版本，未知版本抛 `NotSupportedException` 并停止加载，不能进入损坏恢复分支。
- 活动查询增加 `(OwnerId, Aspect, StartTime)` 的 Segment 部分索引；来源筛选继续使用既有 Source 索引。
- 追加 EF migration `20260911043115_ExplicitFactAspects`，只对 Aspect 为 null 的旧入口调用 SQL 推断；显式值由 EF 写入并保持。Down 不允许退回会重写显式语义的触发器。

预先缺失 Aspect 的旧上传与缓存仍需解释原 `identityKey`/Source。这个解释只存在于兼容边界，
不进入 Dashboard，也不影响显式 Aspect 的新结果。移除门槛见[兼容台账](compatibility-debt.md)。

## 验证入口

- `FactHttpTests.Aspects`：新 Source 复用已知 Aspect；已知 Source 的未知结果不参与分析；完整 JSON、幂等重放、同 Revision 冲突。
- `InProcessCollectorProtocolTranscriptTests.Aspects`：重启保管与包含 Aspect 的精确 ACK。
- `CollectorProtocolClientTests`：旧缓存迁移、未知版本不改文件、不造 Gap。
- `ManagedProcessCollectorProtocolTranscriptTests`：重启后拒绝旧包打开新缓存；更新/回退仍走同一启动入口。
- Browser protocol tests：显式 Aspect 与旧 Hub 能力缺失时不交付；Dashboard Fact Views：语义与 Source 正交。
- `dotnet test Heartbeat.slnx --no-restore`；Browser `npm test`；Frontend `npm run verify`。
