# Shared Kernel — CONTEXT

## Conventions

- **时间存储**：所有时间字段在数据库中以 UTC+0 存储。Dashboard 的“今天”/“本周”由 Browser 按当前 IANA civil timezone 解析为版本化 Local Calendar Window envelope，Analytics 用独立 TZDB 严格重算验证后才查询事实；通用 Instant Window 仍直接传 UTC 起止。
- **认证架构**：依赖外部自建 Auth 平台（支持邮箱/Google/GitHub 登录）。Collection（Agent）持有 Auth 平台签发的 ApiKey，运行时经 `TokenManager` 在 Auth 平台换取短期 session JWT，上传请求携带 `Authorization: Bearer {JWT}`；Dashboard（前端）通过 OIDC 授权码 + PKCE 登录获取 access token。服务端同时接受 OIDC access token 与 Agent session JWT 两种 Bearer 凭证。
- **数据隔离**：多用户模式下，Owner 拥有多个 Subject，用户只能看到属于自己的 Subject 的事实。原生 Fact 上传显式声明 Subject；Machine 关联 Device，Account 与 Person 不借用机器字段。旧缓存导入仍通过 `X-Hardware-Id` 定位历史 Device，所有路径保持同一 OwnerId 隔离不变量。

## Glossary

| Term | Definition |
|------|-----------|
| 观测模型 | 描述观测对象、观测内容、来源、结果与时间之间关系的业务模型；独立于事实如何传输与存储。 |
| 观测对象（FOI） | Collector 直接观察其状态、属性或事件的具体对象，例如设备、浏览器窗口、服务账号或本人；可以只在观测期间存在，生命周期不等于已产生事实的保存期。_Avoid_: 把采集宿主或数据获取入口一律当成观测对象、要求每个 FOI 都有持久化实体。 |
| 对象引用 | 由标识作用域及对象标识解释的对象身份，可用于直接观测对象、结果或来源中的对象。作用域说明标识在哪里有效，不表示统一的父对象或采集器归属。_Avoid_: 用显示名、活动分组或 FactId 代替对象身份。 |
| 对象关系 | 对象之间具有明确业务含义的联系，例如运行于某设备、属于某服务或已确认的使用者关联；随时间变化的关系保留相应时间含义。_Avoid_: 用统一父子关系代替所有联系、由关联自动推断未经观测的事实。 |
| 观测内容 | Collector 关注观测对象的哪项状态、属性或事件，例如窗口的选中页面、账号所在世界或本人的日累计步数。它表达业务含义，不由对象类型或 Fact 家族单独决定。 |
| 观测者 | 执行观测信息获取和解释的 Collector；可以直接观察，也可以读取上游提供的观测结果。 |
| 数据来源 | 观测结果所依据的信息获取途径，例如浏览器 API、系统回调或微信提供的计步读数。_Avoid_: 把读取上游结果的 Collector 一律当成原始测量者。 |
| 观测上下文 | 描述观测对象、观测内容、观测者及必要来源的信息。输出 Fact 时保留解释和使用结果所需的部分，不要求将运行时对象及关系全量持久化或重新构造。 |
| Facts 模型 | 以 Segment、Event、Measurement 三类时间事实表达观测结果的模型；三类事实保留各自的时间及修订语义。_Avoid_: 由观测对象类型直接决定事实家族。 |
| Subject（既有术语） | 既有模型中属于一个 Owner 的事实主体，至少区分 Machine、Account 与 Person；新的直接观测对象模型不以这些类型及既有归属粒度为限制。_Avoid_: Collector Host、把运行 Collector 的机器当成它所观察的对象。 |
| Device | Machine 类型的 Subject，即一台被 system/browser 等 Collector 观察的计算设备。账号和身体不是 Device；它们分别使用 Account 或 Person Subject。_Avoid_: 为了复用机器字段而把账号、身体或 Hub 称为 Device。 |
| App | 用户理解的跨平台应用产品，是 Report、Matcher、Replay 与详情页共同引用的应用身份。`Key` 默认使用稳定、简短的产品 slug（如 `vscode`、`qq`），只在冲突时增加限定词（如 `apple.music`）；`DisplayName` 只负责呈现。一个 App 可由多个 AppIdentity 指向，例如 Windows 与 macOS 的 Visual Studio Code 都归入同一 App。_Avoid_: 把进程名、bundle identifier 或显示名直接当作 App；无冲突时强制添加厂商前缀。 |
| AppIdentity | 平台或系统可直接观测到的应用身份，通过全局唯一 `Key` 显式映射到一个 App，映射是所有 Owner 共享的产品事实：Windows 为 `win:<小写进程名、不含 .exe>`，macOS 为 `mac:<小写 bundle-id>`（缺失时退回小写可执行文件身份），跨平台合成状态使用 `sys:<name>`。未知身份不按名字猜测合并，先建立一对一 provisional App；归并是事务化服务端领域操作，不允许靠直接改单列绕过相关知识与图标处理。 |
| AppUsage | 一段某个 App 处于前台的时间记录（StartTime → EndTime）。system 采集器忠实上报观测到的 AppIdentity，包括 `win:explorer`（桌面）、`win:lockapp`（锁屏）与合成身份 `sys:away`；ActivitySegment 保存 AppIdentityId，Analytics 经 AppIdentity → App 聚合统计。存储上已泛化为 ActivitySegment 的 system source（ADR-017/018 已落地）；`AppUsageItem` 上传 DTO 已随 ADR-020 退役，本词仅指"system 段"这一语义，不再对应独立数据形状。 |
| ActivitySegment | 描述某个被观测活动的 Segment，携带 Source、活动身份及适用的 App 关联；它是 Segment 的一种活动语义，不能代指所有区间事实。Report 只统计 system 的互斥轨，其他 Source 的活动可进入 Replay。 |
| Fact | Collector 对观测对象作出的时间事实，属于 Segment、Event 或 Measurement 三个家族之一。Payload 是可扩展 JSON，无需格式注册；Fact 不提供撤回；配置、命令、Collector Desired State、Package 和叙事知识不是 Fact。_Avoid_: 把统一的事实语义等同于一种固定存储布局。 |
| Segment | 带稳定身份与起止时间的区间事实；合法修订保持身份不变，可以延长区间，也可以纠正结束时间。ActivitySegment 是其中具有活动查询语义的事实。 |
| Event | 发生在一个时刻、没有持续时长的离散事实，InputEvent 是其中的键盘或鼠标事件。_Avoid_: 把 Event 等同于 InputEvent、用零长度 Segment 代替所有事件。 |
| Measurement | 对数值状态或一段时间窗内数值总体的观测，适用于心率、步数与分布等时间序列。Gauge、Sum、Histogram 等成员拥有不同的时间窗、累计、重置与缺失语义，不能退化成统一的“时间戳 + 数字”，也不用 Segment 或 Event 的身份规则强行解释。_Avoid_: Sample（暗示瞬时标量）、Metric Point（基础设施术语，不作产品领域名）。 |
| Fact Stream | 一个 Collector Instance 面向一个 Subject、按同一 Source 产生同一家族 Fact 的稳定流，可容纳多条事实；Activation 只是当前 writer，不属于 Stream 身份。_Avoid_: 把一条 Segment 或其多次修订称为一个 Fact Stream。 |
| FactId | Collector 为一个事实生成的稳定身份，跨重试、重新分批和修订保持不变。 |
| Revision | 同一 Fact 内容演进的单调序号；旧 Revision 不能覆盖新 Revision，更高 Revision 替换内容；Segment 起点和 Event 发生时间保持稳定。 |
| Source | 观测者维度：一条 ActivitySegment 是"谁采集的"（system / browser / vscode / …）。**按观测者命名，不按产品**（ADR-032）：browser 观测几百个产品；同一产品可有多个观测者（规划中的 vrchat.account 云 API / vrchat.client 本机 OSC），因 source 是 ADR-030 声明的主权单位，各自的读数词汇与契约版本独立演化。与 AppId 正交——AppId 说段"关于哪个应用"，Source 说"谁观测到的"；同一时刻同一 App 可有多个 Source 的段合法重叠（对同一事实的独立证据，摄入不去重）。Source 不是 Collector Package、Collector Instance 或 Collector Activation 的身份。system 是唯一观测前台性的 Source，其段互斥、时长可求和。 |
| IdentityKey | 采集器声明的"同一个活动"判据字符串：判据相同 ⇒ 同一活动 ⇒ 同一 Id（快照生长，ADR-018）；旧导入以 (Source, IdentityKey) 做 identity guard，原生事实以 StreamId + FactId + Revision 收敛；回放/查询仍以它分组。browser=规范化 URL（origin+pathname，掐掉 query/fragment；per-domain 覆写表处理"query 即身份"的站点，如 youtube.com/watch 保留 v 参数），完整原始 URL 存 Fact Payload 的 attributes——判据可有损，原始数据无损（ADR-012 原则）。vscode=文件路径，system=AppIdentity+Title（`SystemIdentity.Key`，ADR-020 起由 Agent 客户端计算）。 |
| AppIcon | App 产品对应的图标二进制数据，每个 Owner、每个 App 一份。Agent 以 AppIdentity 上传提示，Analytics 解析到 App 后保留首个有效图标，避免不同平台身份反复覆盖；后续替换走显式刷新。 |
| ApiKey | Auth 平台为 Agent 签发的长期凭证，仅用于向 Auth 平台换取短期 session JWT，不随上传请求直接发送。上传时携带的凭证是换得的 Bearer JWT。_Avoid_: 把 ApiKey 说成"上传凭证"（那是 ADR-004 已退役的旧机制）。 |
| InputEvent | 一次键盘按下或鼠标操作的离散事件记录（一行一事件）。键盘 `Code` 的跨平台规范语义是物理键位置，各平台采集器映射到版本化 code set `heartbeat-key-position-v1`（如 `KeyA`、`Digit1`、`MetaLeft`）；历史 Windows 虚拟键码以 `windows-vk-v1` 解释，不冒充跨平台码。鼠标按钮为 1左/2右/3中，滚轮为 1上/2下；只记按下，KeyUp 仅用于过滤长按自动重复，不落盘。隐私上等价于键盘记录器输出，仅用于单用户自部署的个人统计。主键 Id 为 Agent 生成的 UUIDv7，兼作去重键，保证离线重传幂等。_Avoid_: 把跨平台 `Code` 称作 VK 或 HID。 |
| Replay | 某时间段内 ActivitySegment 的交互式还原视图，用户自己拖时间轴探索。主视图为**注意力线**：单一时间线跟随 system 前台段，存在重叠插件段时段标签升级为插件语义（URL/文件），无插件覆盖的时间窗口 fallback 到窗口标题（ADR-019）。泳道多轨为展开态。_Avoid_: 用 Replay 指代叙事摘要（那是 Recap）。 |
| Recap | 对某时间段的自然语言叙事摘要（"那天你上午在写迁移代码，下午打了三小时 Minecraft"），由 LLM 从 segments 生成，回答"x年前的今天我在做什么"。是 Replay 之上的意义层，也是通往 Replay 的入口。实现见 ADR-023：云端 OpenAI 兼容 LLM（供应商纯配置，先云后本地可逆）、投影/生成两层、缓存按 (Owner, 日窗口) 落库；显式接受标题/URL 出境的单用户 trade-off（与 ADR-012 同格式）。属 Analytics 上下文，详见 server/CONTEXT.md。 |

## Anti-goals

- **不做电影化回放**（配乐、节奏剪辑、自动生成影片）。Heartbeat 数据源（窗口标题、按键、URL）没有照片级情感密度，正确美学是档案馆与日记，不是 MV。
