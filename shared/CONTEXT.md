# Shared Kernel — CONTEXT

## Conventions

- **时间存储**：所有时间字段在数据库中以 UTC+0 存储。"今天"/"本周"的边界由前端根据用户浏览器时区确定，通过 DateTimeOffset 参数传给服务端。
- **认证架构**：依赖外部自建 Auth 平台（支持邮箱/Google/GitHub 登录）。Collection（Agent）持有 Auth 平台签发的 ApiKey，运行时经 `TokenManager` 在 Auth 平台换取短期 session JWT，上传请求携带 `Authorization: Bearer {JWT}`；Dashboard（前端）通过 OIDC 授权码 + PKCE 登录获取 access token。服务端同时接受 OIDC access token 与 Agent session JWT 两种 Bearer 凭证。
- **数据隔离**：多用户模式下，User 拥有多个 Device，AppUsage 通过 Device 间接关联到 User，用户只能看到自己 Device 的数据。所有业务端点需 JWT 认证，Service 层显式按 OwnerId 过滤。Agent 通过 `X-Hardware-Id` header 标识设备，服务端用 (OwnerId, HardwareId) 定位设备，首次见到新组合时自动创建。

## Glossary

| Term | Definition |
|------|-----------|
| Device | 一台唯一的物理机器。由 (OwnerId, HardwareId) 联合唯一标识。HardwareId 取自 Windows MachineGuid（HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid）。DeviceName 为纯显示字段（默认取 hostname，用户可改）。属于某个 User（OwnerId = JWT sub claim，string 类型）。 |
| App | 一个应用程序，由进程可执行文件名（不含路径）唯一标识。同一 exe 无论开几个窗口都算同一个 App。 |
| AppUsage | 一段某个 App 处于前台的时间记录（StartTime → EndTime）。系统忠实记录所有前台窗口，包括 explorer.exe（桌面）和 LockApp.exe（锁屏），不做活跃/非活跃过滤。存储上已泛化为 ActivitySegment 的 system source（ADR-017/018 已落地）；`AppUsageItem` 上传 DTO 已随 ADR-020 退役，本词仅指"system 段"这一语义，不再对应独立数据形状。 |
| ActivitySegment | 一段有界的活动记录（StartTime → EndTime），由某个采集器（Source）观测并折叠产出。瞬时点事件为零长度段（StartTime == EndTime）。AppUsage 的泛化形态；统计只消费 source='system'（互斥轨），插件段只进回放。详见 ADR-017/018/020。 |
| Source | 观测者维度：一条 ActivitySegment 是"谁采集的"（system / browser / vscode / …）。与 AppId 正交——AppId 说段"关于哪个应用"，Source 说"谁观测到的"；同一时刻同一 App 可有多个 Source 的段合法重叠。system 是唯一观测前台性的 Source，其段互斥、时长可求和。 |
| IdentityKey | 采集器声明的"同一个活动"判据字符串：判据相同 ⇒ 同一活动 ⇒ 同一 Id（快照生长，ADR-018）；服务端以 (Source, IdentityKey) 做 upsert 的 identity guard，回放/查询以它分组。browser=规范化 URL（origin+pathname，掐掉 query/fragment；per-domain 覆写表处理"query 即身份"的站点，如 youtube.com/watch 保留 v 参数），完整原始 URL 存 Attributes——判据可有损，原始数据无损（ADR-012 原则）。vscode=文件路径，system=App+Title（`SystemIdentity.Key`，ADR-020 起由 Agent 客户端计算）。 |
| AppIcon | App 对应的图标二进制数据，由 Agent 上传，供 Dashboard 展示。 |
| ApiKey | Auth 平台为 Agent 签发的长期凭证，仅用于向 Auth 平台换取短期 session JWT，不随上传请求直接发送。上传时携带的凭证是换得的 Bearer JWT。_Avoid_: 把 ApiKey 说成"上传凭证"（那是 ADR-004 已退役的旧机制）。 |
| InputEvent | 一次键盘按下或鼠标操作的离散事件记录（一行一事件）。由 Agent 通过全局低级钩子（WH_KEYBOARD_LL/WH_MOUSE_LL）采集。EventType 区分 KeyDown(1)/MouseButton(2)/MouseScroll(3)；Code 在键盘事件中为 Windows 虚拟键码原始值，鼠标按钮为 1左/2右/3中，滚轮为 1上/2下。只记按下，KeyUp 仅用于过滤长按自动重复，不落盘。隐私上等价于键盘记录器输出，仅用于单用户自部署的个人统计。主键 Id 为 Agent 生成的 UUIDv7，兼作去重键，保证离线重传幂等（服务端 ON CONFLICT DO NOTHING）。 |
| Replay | 某时间段内 ActivitySegment 的交互式还原视图，用户自己拖时间轴探索。主视图为**注意力线**：单一时间线跟随 system 前台段，存在重叠插件段时段标签升级为插件语义（URL/文件），无插件覆盖的时间窗口 fallback 到窗口标题（ADR-019）。泳道多轨为展开态。_Avoid_: 用 Replay 指代叙事摘要（那是 Recap）。 |
| Recap | 对某时间段的自然语言叙事摘要（"那天你上午在写迁移代码，下午打了三小时 Minecraft"），由 LLM 从 segments 生成，回答"x年前的今天我在做什么"。是 Replay 之上的意义层，也是通往 Replay 的入口。实现见 ADR-023：云端 OpenAI 兼容 LLM（供应商纯配置，先云后本地可逆）、投影/生成两层、缓存按 (Owner, 日窗口) 落库；显式接受标题/URL 出境的单用户 trade-off（与 ADR-012 同格式）。属 Analytics 上下文，详见 server/CONTEXT.md。 |

## Anti-goals

- **不做电影化回放**（配乐、节奏剪辑、自动生成影片）。Heartbeat 数据源（窗口标题、按键、URL）没有照片级情感密度，正确美学是档案馆与日记，不是 MV。
