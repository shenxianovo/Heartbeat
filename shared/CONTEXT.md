# Shared Kernel — CONTEXT

2026-09-11：当前领域目标见 [Observations 与 Facts](../docs/architecture/observations-model.md)。
五表存储及追加迁移已实现，业务库未迁移；下文 Target、应用上下文和传输术语继续解释上传转换及历史资料。

## Conventions

- **时间存储**：所有时间字段在数据库中以 UTC+0 存储。Dashboard 的“今天”/“本周”由 Browser 按当前 IANA civil timezone 解析为版本化 Local Calendar Window envelope，Analytics 用独立 TZDB 严格重算验证后才查询事实；通用 Instant Window 仍直接传 UTC 起止。
- **认证架构**：依赖外部自建 Auth 平台（支持邮箱/Google/GitHub 登录）。Collection（Agent）持有 Auth 平台签发的 ApiKey，运行时经 `TokenManager` 在 Auth 平台换取短期 session JWT，上传请求携带 `Authorization: Bearer {JWT}`；Dashboard（前端）通过 OIDC 授权码 + PKCE 登录获取 access token。服务端同时接受 OIDC access token 与 Agent session JWT 两种 Bearer 凭证。
- **数据隔离**：Owner 是事实数据的所有权边界；观测归属与对象关系不改变所有权。当前运行代码仍使用 Observer/Target；新目标的引用与查询同样必须保持 Owner 隔离。

## Glossary

| Term | Definition |
|------|-----------|
| Observations（观测模型） | 以 Observer、FOI 描述事实产生的观测关系：Observer 观察 FOI，产生 Facts。Aspect、结果及适用时间由 Facts 表达；该模型独立于传输与存储布局。 |
| FeatureOfInterest / FOI（观测对象） | Collector 直接描述其状态、属性或事件的对象；当前范围为机器、App 产品、服务账号和个人。对象可以独立存在，不以设备/App 运行关系齐全为前提。_Avoid_: 把采集宿主自动当作 FOI、要求先组合设备与 App 才能描述应用。 |
| Target（既有术语） | 上一轮设计及当前上传转换中的事实归属对象；应用上下文也可作为该归属。新模型直接描述 FOI，不再要求额外 Target；该术语继续解释上传转换与历史资料。 |
| 对象引用 | 由标识作用域及对象标识解释的对象身份，可用于直接观测对象、结果或来源中的对象。作用域说明标识在哪里有效，不表示统一的父对象或采集器归属。_Avoid_: 用显示名、活动分组或 FactId 代替对象身份。 |
| 对象关系 | 对象之间具有明确业务含义的联系，有参与对象及其角色、已确认的适用范围和依据；可由两个或更多对象共同参与，例如某台机器上的应用使用某账号。关系缺失不阻止保存事实。_Avoid_: 由关联推断未经观测的事实、将时间未知当作无限有效。 |
| 使用者关联 | 对账号等对象与其使用者之间关系的明确确认，具有已确认的适用范围；可后补，用于汇集该范围内已有的相关事实，不改变事实的直接观测对象与来源。_Avoid_: 仅凭相同 Owner 推定使用者、把关联建立时间当成其生效时间。 |
| Aspect（观测内容） | Fact 描述对象的什么属性、状态或事件，例如应用的选中页面、账号所在世界或本人的日累计步数；与 Result、Time 共同表达事实。_Avoid_: 用对象类别或 Fact 家族代替具体观测含义。 |
| Result（观测结果） | Fact 对其 Aspect 给出的具体结果；可为结构化内容或对象引用，与观测对象和适用时间一起解释。 |
| Time（适用时间） | Fact 的结果所适用的时刻或区间，不等于接收、上传或关系登记时间。Measurement 既可适用于时刻，也可适用于区间。 |
| Observer（观测者） | 实际执行观测信息获取和解释的具体 Collector，身份跨进程重启、停止后继续及更新保持；Mac1 与 Windows1 上的 System Collector 是不同 Observer，拥有相同的 Collector 类型。一个 Observer 可观察多个 FOI，同一个 FOI 也可被多个 Observer 观察。 |
| 观测环境 | 解释观测对象所处环境的已知信息，例如浏览器窗口所在的设备；与直接观测对象及数据来源分别表达。_Avoid_: 把采集宿主自动当成被观测活动发生的设备。 |
| 观测上下文 | 说明观测对象、观测者及必要观测细节的信息，用于解释观测结果的含义；与表示设备上某个 App 环境的应用上下文区分。 |
| Facts 模型 | 以 Segment、Event、Measurement 三类时间事实表达观测结果的模型；每条 Fact 表达某个 Aspect 的结果及适用时间，各家族保留自己的时间及修订语义。_Avoid_: 由观测对象类型直接决定事实家族。 |
| Subject（既有术语） | 既有 Machine、Account、Person 传输/管理分组，继续用于流身份、旧缓存接管和管理授权；不再是新事实的业务归属。_Avoid_: 用 Stream → Subject 解释事实 Target、把采集宿主当成被观测对象。 |
| Device | 一台可被观察的计算设备，可独立成为 FOI，也可参与明确的安装或使用关系。账号、本人和采集器宿主不能仅因运行位置被当作被观测设备。 |
| App | 用户理解的跨平台应用产品，是 Report、Matcher、Replay 与详情页共同引用的应用身份。`Key` 默认使用稳定、简短的产品 slug（如 `vscode`、`qq`），只在冲突时增加限定词（如 `apple.music`）；`DisplayName` 只负责呈现。一个 App 可由多个 AppIdentity 指向，例如 Windows 与 macOS 的 Visual Studio Code 都归入同一 App。_Avoid_: 把进程名、bundle identifier 或显示名直接当作 App；无冲突时强制添加厂商前缀。 |
| 应用上下文（既有术语） | 上一轮设计及当前实现中由设备与 App 产品共同辨认的应用环境，跨启动保持且不按 Profile 区分。新模型不要求它成为独立实体，设备与 App 的联系按明确关系表达。 |
| AppIdentity | 平台或系统可直接观测到的应用身份，通过全局唯一 `Key` 显式映射到一个 App，映射是所有 Owner 共享的产品事实：Windows 为 `win:<小写进程名、不含 .exe>`，macOS 为 `mac:<小写 bundle-id>`（缺失时退回小写可执行文件身份），跨平台合成状态使用 `sys:<name>`。未知身份不按名字猜测合并，先建立一对一 provisional App；归并是事务化服务端领域操作，不允许靠直接改单列绕过相关知识与图标处理。 |
| AppUsage | 一段某个 App 处于前台的时间记录（StartTime → EndTime）。system 采集器忠实上报观测到的 AppIdentity，包括 `win:explorer`（桌面）、`win:lockapp`（锁屏）与合成身份 `sys:away`；ActivitySegment 保存 AppIdentityId，Analytics 经 AppIdentity → App 聚合统计。存储上已泛化为 ActivitySegment 的 system source（ADR-017/018 已落地）；`AppUsageItem` 上传 DTO 已随 ADR-020 退役，本词仅指"system 段"这一语义，不再对应独立数据形状。 |
| ActivitySegment | 描述某个被观测活动的 Segment，携带 Source、活动身份及适用的 App 关联；它是 Segment 的一种活动语义，不能代指所有区间事实。Report 只统计 system 的互斥轨，其他 Source 的活动可进入 Replay。 |
| Fact | Collector 对一个 FOI 作出的时间事实，包含 Aspect、Result、Time，属于 Segment、Event 或 Measurement。配置、命令、Collector Desired State、Package 和叙事知识不是 Fact；当前不提供撤回。_Avoid_: 因同一 FOI 而合并独立事实。 |
| Segment | 带稳定身份与起止时间的区间事实；合法修订保持身份不变，可以延长区间，也可以纠正结束时间。ActivitySegment 是其中具有活动查询语义的事实。 |
| Event | 发生在一个时刻、没有持续时长的离散事实，InputEvent 是其中的键盘或鼠标事件。_Avoid_: 把 Event 等同于 InputEvent、用零长度 Segment 代替所有事件。 |
| Measurement | 对数值状态或一段时间窗内数值总体的观测，适用于心率、步数与分布等时间序列。Gauge、Sum、Histogram 等成员拥有不同的时间窗、累计、重置与缺失语义，不能退化成统一的“时间戳 + 数字”，也不用 Segment 或 Event 的身份规则强行解释。_Avoid_: Sample（暗示瞬时标量）、Metric Point（基础设施术语，不作产品领域名）。 |
| Fact Stream | 当前 Collector 交付同一来源、同一家族 Facts 的稳定流，保留既有 Instance、传输 Subject 与输出维度的身份约束；Activation 只是当前 writer。它是交付概念，不决定 FOI；新存储目标下的身份与重放衔接待迁移设计。 |
| FactId | Collector 为一个事实生成的稳定身份，跨重试、重新分批和修订保持不变。 |
| Revision | 同一 Fact 内容演进的单调序号；旧 Revision 不能覆盖新 Revision，更高 Revision 替换内容；Segment 起点和 Event 发生时间保持稳定。 |
| Source | 观测来源类型维度：一条 ActivitySegment 是"谁采集的"（system / browser / vscode / …）。**按观测者命名，不按产品**（ADR-032）：browser 观测几百个产品；同一产品可有多个观测者（规划中的 vrchat.account 云 API / vrchat.client 本机 OSC），因 source 是 ADR-030 声明的主权单位，各自的读数词汇与契约版本独立演化。与 AppId 正交——AppId 说段"关于哪个应用"，Source 说"谁观测到的"；同一时刻同一 App 可有多个 Source 的段合法重叠（对同一事实的独立证据，摄入不去重）。Source 不是具体 Observer、Collector Package、Collector Instance 或 Collector Activation 的身份。system 是唯一观测前台性的 Source，其段互斥、时长可求和。 |
| IdentityKey | 采集器声明的"同一个活动"判据字符串：判据相同 ⇒ 同一活动 ⇒ 同一 Id（快照生长，ADR-018）；旧导入以 (Source, IdentityKey) 做 identity guard，原生事实以 StreamId + FactId + Revision 收敛；回放/查询仍以它分组。browser=规范化 URL（origin+pathname，掐掉 query/fragment；per-domain 覆写表处理"query 即身份"的站点，如 youtube.com/watch 保留 v 参数），完整原始 URL 存 Fact Payload 的 attributes——判据可有损，原始数据无损（ADR-012 原则）。vscode=文件路径，system=AppIdentity+Title（`SystemIdentity.Key`，ADR-020 起由 Agent 客户端计算）。 |
| AppIcon | App 产品对应的图标二进制数据，每个 Owner、每个 App 一份。Agent 以 AppIdentity 上传提示，Analytics 解析到 App 后保留首个有效图标，避免不同平台身份反复覆盖；后续替换走显式刷新。 |
| ApiKey | Auth 平台为 Agent 签发的长期凭证，仅用于向 Auth 平台换取短期 session JWT，不随上传请求直接发送。上传时携带的凭证是换得的 Bearer JWT。_Avoid_: 把 ApiKey 说成"上传凭证"（那是 ADR-004 已退役的旧机制）。 |
| InputEvent | 一次键盘按下或鼠标操作的离散事件记录（一行一事件）。键盘 `Code` 的跨平台规范语义是物理键位置，各平台采集器映射到版本化 code set `heartbeat-key-position-v1`（如 `KeyA`、`Digit1`、`MetaLeft`）；历史 Windows 虚拟键码以 `windows-vk-v1` 解释，不冒充跨平台码。鼠标按钮为 1左/2右/3中，滚轮为 1上/2下；只记按下，KeyUp 仅用于过滤长按自动重复，不落盘。隐私上等价于键盘记录器输出，仅用于单用户自部署的个人统计。主键 Id 为 Agent 生成的 UUIDv7，兼作去重键，保证离线重传幂等。_Avoid_: 把跨平台 `Code` 称作 VK 或 HID。 |
| Replay | 某时间段内 ActivitySegment 的交互式还原视图，用户自己拖时间轴探索。主视图为**注意力线**：单一时间线跟随 system 前台段，存在重叠插件段时段标签升级为插件语义（URL/文件），无插件覆盖的时间窗口 fallback 到窗口标题（ADR-019）。泳道多轨为展开态。_Avoid_: 用 Replay 指代叙事摘要（那是 Recap）。 |
| Recap | 对某时间段的自然语言叙事摘要（"那天你上午在写迁移代码，下午打了三小时 Minecraft"），由 LLM 从 segments 生成，回答"x年前的今天我在做什么"。是 Replay 之上的意义层，也是通往 Replay 的入口。实现见 ADR-023：云端 OpenAI 兼容 LLM（供应商纯配置，先云后本地可逆）、投影/生成两层、缓存按 (Owner, 日窗口) 落库；显式接受标题/URL 出境的单用户 trade-off（与 ADR-012 同格式）。属 Analytics 上下文，详见 server/CONTEXT.md。 |

## Anti-goals

- **不做电影化回放**（配乐、节奏剪辑、自动生成影片）。Heartbeat 数据源（窗口标题、按键、URL）没有照片级情感密度，正确美学是档案馆与日记，不是 MV。
