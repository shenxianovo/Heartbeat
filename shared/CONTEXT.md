# Shared Kernel — CONTEXT

## Conventions

- **时间存储**：所有时间字段在数据库中以 UTC+0 存储。"今天"/"本周"的边界由前端根据用户浏览器时区确定，通过 DateTimeOffset 参数传给服务端。
- **认证架构**：依赖外部自建 Auth 平台（支持邮箱/Google/GitHub 登录）。Collection（Agent）持有 Auth 平台签发的 ApiKey，运行时经 `TokenManager` 在 Auth 平台换取短期 session JWT，上传请求携带 `Authorization: Bearer {JWT}`；Dashboard（前端）通过 OIDC 授权码 + PKCE 登录获取 access token。服务端同时接受 OIDC access token 与 Agent session JWT 两种 Bearer 凭证。
- **数据隔离**：多用户模式下，User 拥有多个 Device，AppUsage 通过 Device 间接关联到 User，用户只能看到自己 Device 的数据。所有业务端点需 JWT 认证，Service 层显式按 OwnerId 过滤。Agent 通过 `X-Hardware-Id` header 标识设备，服务端用 (OwnerId, HardwareId) 定位设备，首次见到新组合时自动创建。

## Glossary

| Term | Definition |
|------|-----------|
| Device | 一个**观测主体**（ADR-032）：某采集器如实观测的对象——一台机器（桌面 Agent 观测本机），或一个账号（如无头 hub 的 vrchat.account 采集器观测 VRChat 账号）。由 (OwnerId, HardwareId) 联合唯一标识；机器主体的 HardwareId 取平台原生稳定机器标识（Windows `MachineGuid`、macOS `IOPlatformUUID`），允许不同平台具有不同的系统重装稳定性；无头 hub 的账号主体由配置给定稳定串。DeviceName 为纯显示字段（默认取 hostname，用户可改）。属于某个 User（OwnerId = JWT sub claim，string 类型）。观测主体必须如实命名：账号采集器证明不了硬件，其 Device 是账号（"VRChat"），不是猜测的硬件（"Quest 3"）——设备归因是叙事层派生知识，不进事实层。 |
| App | 用户理解的跨平台应用产品，是 Report、Matcher、Replay 与详情页共同引用的应用身份。`Key` 默认使用稳定、简短的产品 slug（如 `vscode`、`qq`），只在冲突时增加限定词（如 `apple.music`）；`DisplayName` 只负责呈现。一个 App 可由多个 AppIdentity 指向，例如 Windows 与 macOS 的 Visual Studio Code 都归入同一 App。_Avoid_: 把进程名、bundle identifier 或显示名直接当作 App；无冲突时强制添加厂商前缀。 |
| AppIdentity | 平台或系统可直接观测到的应用身份，通过全局唯一 `Key` 显式映射到一个 App，映射是所有 Owner 共享的产品事实：Windows 为 `win:<小写进程名、不含 .exe>`，macOS 为 `mac:<小写 bundle-id>`（缺失时退回小写可执行文件身份），跨平台合成状态使用 `sys:<name>`。未知身份不按名字猜测合并，先建立一对一 provisional App；归并是事务化服务端领域操作，不允许靠直接改单列绕过相关知识与图标处理。 |
| AppUsage | 一段某个 App 处于前台的时间记录（StartTime → EndTime）。system 采集器忠实上报观测到的 AppIdentity，包括 `win:explorer`（桌面）、`win:lockapp`（锁屏）与合成身份 `sys:away`；ActivitySegment 保存 AppIdentityId，Analytics 经 AppIdentity → App 聚合统计。存储上已泛化为 ActivitySegment 的 system source（ADR-017/018 已落地）；`AppUsageItem` 上传 DTO 已随 ADR-020 退役，本词仅指"system 段"这一语义，不再对应独立数据形状。 |
| ActivitySegment | 一段有界的活动记录（StartTime → EndTime），由某个采集器（Source）观测并折叠产出。瞬时点事件为零长度段（StartTime == EndTime）。AppUsage 的泛化形态；统计只消费 source='system'（互斥轨），插件段只进回放。详见 ADR-017/018/020。 |
| Source | 观测者维度：一条 ActivitySegment 是"谁采集的"（system / browser / vscode / …）。**按观测者命名，不按产品**（ADR-032）：browser 观测几百个产品；同一产品可有多个观测者（规划中的 vrchat.account 云 API / vrchat.client 本机 OSC），因 source 是 ADR-030 声明的主权单位，各自的读数词汇与契约版本独立演化。与 AppId 正交——AppId 说段"关于哪个应用"，Source 说"谁观测到的"；同一时刻同一 App 可有多个 Source 的段合法重叠（对同一事实的独立证据，摄入不去重）。system 是唯一观测前台性的 Source，其段互斥、时长可求和。 |
| IdentityKey | 采集器声明的"同一个活动"判据字符串：判据相同 ⇒ 同一活动 ⇒ 同一 Id（快照生长，ADR-018）；服务端以 (Source, IdentityKey) 做 upsert 的 identity guard，回放/查询以它分组。browser=规范化 URL（origin+pathname，掐掉 query/fragment；per-domain 覆写表处理"query 即身份"的站点，如 youtube.com/watch 保留 v 参数），完整原始 URL 存 Attributes——判据可有损，原始数据无损（ADR-012 原则）。vscode=文件路径，system=AppIdentity+Title（`SystemIdentity.Key`，ADR-020 起由 Agent 客户端计算）。 |
| AppIcon | App 产品对应的图标二进制数据，每个 Owner、每个 App 一份。Agent 以 AppIdentity 上传提示，Analytics 解析到 App 后保留首个有效图标，避免不同平台身份反复覆盖；后续替换走显式刷新。 |
| ApiKey | Auth 平台为 Agent 签发的长期凭证，仅用于向 Auth 平台换取短期 session JWT，不随上传请求直接发送。上传时携带的凭证是换得的 Bearer JWT。_Avoid_: 把 ApiKey 说成"上传凭证"（那是 ADR-004 已退役的旧机制）。 |
| InputEvent | 一次键盘按下或鼠标操作的离散事件记录（一行一事件）。键盘 `Code` 的跨平台规范语义是物理键位置，各平台采集器映射到版本化 code set `heartbeat-key-position-v1`（如 `KeyA`、`Digit1`、`MetaLeft`）；历史 Windows 虚拟键码以 `windows-vk-v1` 解释，不冒充跨平台码。鼠标按钮为 1左/2右/3中，滚轮为 1上/2下；只记按下，KeyUp 仅用于过滤长按自动重复，不落盘。隐私上等价于键盘记录器输出，仅用于单用户自部署的个人统计。主键 Id 为 Agent 生成的 UUIDv7，兼作去重键，保证离线重传幂等。_Avoid_: 把跨平台 `Code` 称作 VK 或 HID。 |
| Replay | 某时间段内 ActivitySegment 的交互式还原视图，用户自己拖时间轴探索。主视图为**注意力线**：单一时间线跟随 system 前台段，存在重叠插件段时段标签升级为插件语义（URL/文件），无插件覆盖的时间窗口 fallback 到窗口标题（ADR-019）。泳道多轨为展开态。_Avoid_: 用 Replay 指代叙事摘要（那是 Recap）。 |
| Recap | 对某时间段的自然语言叙事摘要（"那天你上午在写迁移代码，下午打了三小时 Minecraft"），由 LLM 从 segments 生成，回答"x年前的今天我在做什么"。是 Replay 之上的意义层，也是通往 Replay 的入口。实现见 ADR-023：云端 OpenAI 兼容 LLM（供应商纯配置，先云后本地可逆）、投影/生成两层、缓存按 (Owner, 日窗口) 落库；显式接受标题/URL 出境的单用户 trade-off（与 ADR-012 同格式）。属 Analytics 上下文，详见 server/CONTEXT.md。 |

## Anti-goals

- **不做电影化回放**（配乐、节奏剪辑、自动生成影片）。Heartbeat 数据源（窗口标题、按键、URL）没有照片级情感密度，正确美学是档案馆与日记，不是 MV。
