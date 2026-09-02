# Analytics

接收采集数据，以快照 upsert 持久化，聚合报表，向 Dashboard 提供只读 API。

## Language

**Ingest（摄入）**:
统一摄入例程（ADR-018）：校验 → App 关联 → 按 Id 快照 upsert。摄入可交换、可重入——乱序重传与批内同 Id 快照收敛到同一行。上传入口收敛为 `/segments` 单条（ADR-020）：system 段由 Agent 客户端算好 IdentityKey 经同一入口上传；`POST /usage` 及其映射层已退役，`GET /usage` 仅存查询投影。
整批 strict ingest 的 contract validation、Device 解析、AppIdentity/ActivitySegment 投影与 commit 由 `ISegmentIngestApplicationService` 作为一个 unit-of-work 边界拥有；HTTP Controller 只提取协议字段并映射可判定结果。
_Avoid_: Merge、续接（ADR-001 的服务端合并已被 ADR-018 快照 upsert 取代；CanMerge 一词只存在于历史 ADR 中）

**Snapshot Upsert（快照 upsert）**:
Id 即活动身份：已有行则单调生长（EndTime 取 max、attributes 后写胜），新 Id 插入。采集端对同一活动多次上报的是同一 Id 的更大快照，不是新段。

**Report（报表）**:
对某 Owner 某时间窗的聚合视图（daily / weekly）。只消费 system 段——互斥轨，时长可求和；插件段只进回放，不进统计（ADR-017 §4 统计边界）。跨窗段做区间重叠 + 裁剪：只计落在窗口内的部分，不漏不双计（ADR-018 §4）。
_Avoid_: Statistics, Summary

**App**:
跨平台应用产品，是统计聚合、Matcher 与详情查询的主维度。稳定 Key 承载知识引用，DisplayName 是可更新文案；可同时安装和运行的发行渠道或产品变体默认是不同 App。摄入时由 AppIdentityKey 解析并让 ActivitySegment 引用 AppIdentity；多个平台身份映射到同一 App，查询经 AppIdentity → App 聚合。已知产品由 App Catalog 确定映射；未知身份先创建一对一 provisional App，不按名称猜测归并。presence 同样接收 AppIdentityKey，并向 Dashboard 投影 App 的 Id/Key/DisplayName。AppIcon 由 Agent 以 AppIdentity 上传提示，每个 Owner/App 保留一份产品图标。

**App Catalog（应用目录）**:
服务端维护的部署全局产品目录，声明经过真实设备观察或供应商资料确认的已知 App 规范 Key、DisplayName 与一个或多个平台 AppIdentity。单平台稳定产品同样可以进入目录；provisional 表示产品尚未被系统识别，不表示它缺少跨平台版本。已发布 Key 与 identity 映射默认只增不删；错误映射通过显式迁移修正。目录内身份确定映射；目录外身份保留为 provisional App，部署管理员可以显式补充或修正映射。
_Avoid_: 名称相似度自动归并、每个 Owner 各自维护产品映射、把 App Catalog 当作已安装应用清单、在目录中塞报表隐藏或统计行为规则

**App Catalog Override（应用目录覆盖）**:
部署管理员对单个 AppIdentity 归属作出的部署本地决定，指向一个目标 App，优先于内置 App Catalog，并保留修改人和时间。AppIdentity.AppId 是当前生效结果；Override 是协调器必须长期尊重的管理员意图，不能从当前外键反向猜测。删除 Override 后立即重新协调：命中 Catalog 则恢复内置映射，否则拆回独立 provisional App。
_Avoid_: 直接改 AppIdentity.AppId 充当覆盖、把 merge receipt 当作当前覆盖状态、按 Owner 建覆盖

**Deployment Administrator（部署管理员）**:
JWT `sub` 出现在部署环境白名单中的用户，可以管理影响所有 Owner 的 App Catalog 映射。Auth 平台负责让部署者取得不可变 `sub`；Heartbeat 只判断当前用户是否为部署管理员，不允许从产品 UI 授予或撤销该权限。
_Avoid_: 用可变 username 授权、把普通 Owner 自动视为部署管理员、在 App Catalog 页面管理管理员权限

**Owner / Subject**:
Owner 是事实的数据主人；Subject 是 Collector 如实观察的对象，可以是 Machine、Account 或 Person。Device 只指 Machine Subject；账号、身体和运行无头 Hub 的服务器都不能为了复用设备维度而冒充事实主体（ADR-041，词条详见 shared/CONTEXT.md）。
_Avoid_: 把 Hub Instance 当 Subject、把 Account 或 Person 称为 Device、用事实主体记录猜测的硬件归因

**User Provisioning（用户供给）**:
懒建，由**本人首次带 JWT 的请求**触发：upsert User 行（`Id = sub`，`Username = preferred_username`，默认 private）。匿名按用户名读取只查本地 Users 表，查不到即 404——不回源 Auth 平台、不建行（防爬虫刷空行 + 用户名枚举）。**sub-first 规则**：带 JWT 请求一律用 `sub` 定位 User 行，Username 只是可刷新的显示缓存 + 匿名查询入口。username 可变（AuthService 改名立即释放旧名，GitHub 模式）：供给回写含**驱逐**——同名异 sub 的 stale 行被改为 `~{sub}` 占位（`~` 不在上游字符集，永不撞真名），被驱逐者下次带 JWT 请求自愈。设计定于 2026-07-17（ADR-027）。
_Avoid_: 注册/Registration（Heartbeat 无注册概念，账号归 Auth 平台；显式注册流程留给未来隐私条款同意场景）

**Dashboard Visibility（看板可见性）**:
每个 User 一个 `IsPublic` 开关，默认 private。private 时按用户名的匿名读路径一律 404（不泄露用户名存在性）；本人经 JWT（`sub == User.Id`）读自己的数据，不受开关影响。当前阶段 public = 全保真放行现有读端点（决策：先 A 后 C——待自定义看板落地后升级为每卡片可见性配置，全保真公开届时退役）。
_Avoid_: Public Profile（GitHub 语义是粗聚合展示，这里 public 是全量数据，语义不同不要混用）

**Recap（叙事摘要）**:
对某 Owner 某个已验证 Local Calendar Window 的 LLM 叙事视图：Observation 是事实，Episode 与 Strand 提供用户确认的当天事实和持续语境，Recap 是可重生成的派生物。缓存、生成、投影、新鲜度与生成锁共享 `(OwnerId, WindowKey)` 身份；今天的 Segment 水位与历史知识变化都只提示“可重新生成”，不由读取自动消耗 LLM。跨设备聚合，无 deviceId 维度；口吻是日记/档案，只叙事，不评判、不打分、不建议（ADR-023/031/042/044）。
_Avoid_: Summary（Report 词条同禁）、日报（汇报工具的词，Recap 是记忆）、把 Recap 正文当事实库

**Recap Projection（Recap 投影）**:
segments → LLM 输入的确定性压缩（纯函数，可单测）：system 段按设备分轨作注意力骨架（轨内互斥、带时长），插件段按 IdentityKey 聚合作语义细节轨；碎段合并/丢弃只影响投影不动数据。digest 的身份维度按观测深度长成深度树（块内下一深度分解、预算剪枝，ADR-029），叙事与发问两次调用共用同一 digest。未来外部 Agent/MCP 能力暴露的开门处（不预建，ADR-023 §2）。
_Avoid_: 复刻标签升级喂单线（ADR-019 是展示层且有损，被 ADR-023 §3 否决）

**Strand（脉络）**:
用户确认的、跨日期延续的私人叙事语境。Strand 组成严格单父级、无环、无固定层数/类型的树；节点有名字、自由释义、可选的近似起止日期和 Matcher 指纹，父节点可以是零 Matcher 的纯语境容器。命中子节点时带入完整祖先链，命中父节点不激活后代；现实归属变化以结束旧 Strand、创建新 Strand 表达，移动只用于纠错（ADR-031）。
_Avoid_: Project（太窄）、Tag、Note、Task；用 Strand 记录每次临时行为；把 Matcher 命中当 Segment 归属

**Activity Cluster（活动簇）**:
模型从完整时间线临时归纳出的跨 Source 证据视图，带大概时间区间，只用于发问和帮助用户回忆；它不是用户事实，不持久化，也不拥有 Segment。用户确认后可产生 Episode、Strand 或两者。
_Avoid_: Episode（Episode 必须经用户确认）、持久化活动分类、精确工时区间

**Episode（片段事实）**:
用户确认的、有大概时间边界的一次具体发生；可独立存在，或至多关联一个最具体 Strand 并继承其祖先语境。Episode 不在 Strand 树中、不拥有 Segment、不会因 Matcher 命中自动生成；“提升”为 Strand 是保留 Episode 后新增/关联持续脉络，不是类型转换。
_Avoid_: RecapNote（过于 UI 化）、ActivityCluster（未经确认）、Strand 叶节点、Task Log

**Recurrence Probe（复现探针）**:
附在“尚不确定是否持续”的 Episode 上、由用户确认的高精度观察谓词。命中只让 Asking 建议是否提升为 Strand，不向 Recap 注入旧 Episode、不自动归属或建 Strand；提升、否认或静音后即解决。
_Avoid_: Strand Matcher（命中后果不同）、自动提升规则、后台分类器

**Observation Depth（观测深度）**:
每个采集器在自身实现里声明、**运行时经注册通道上报**的有序观测读数表，浅 → 深（ADR-030）：声明 = {source, 契约版本, layers:[{readings:[{name, from, label}]}]}；读数命名与人话标签归采集器主权（name 在 source 内唯一），`from` 只指运输槽位（appName / title / identityKey / attributes.*，新读数一律走 attributes.*）——服务端是按槽取值的无关层，不认识 app / url / site 这些词。生效表 = 每 source 取 max(版本)；未声明 source 走通用回落（L1 identity / L2 title）；读时取值，历史 segments 被新声明自动覆盖。Analytics 启动只为 System BuiltIn 预插声明，非 BuiltIn Collector 必须运行时上报。现行表——system：进程/App → 窗口标题；browser：站点(site, eTLD+1) → URL → 标签页标题；vscode 规划：仓库根 → 文件路径。**单读数值空间内部的层级（域后缀 / 路径前缀）默认归谓词轴；digest 粗档证据需要时由采集器提拔为独立读数层**（版本+1，服务端零改动）。**同时是隐私敏感度轴**——与 ADR-017"采集能力分层可拆"是同一张表。digest 的身份维度按它长成**深度树**：节点 = (读数值, 并集时长)，子节点 = 下一深度分解，缺读数段挂最深可用读数；渲染 = 确定性预算剪枝（展开门槛、子数封顶、尾部折叠）。
_Avoid_: 粒度（粗细是谓词维度，不是深度）；在知识层写死采集器字段名或读数词汇（server 侧不得出现 per-source 分支——ADR-030 前的"镜像"写法已退役）

**Matcher（匹配子）**:
沿某 Source 观测深度树的路径谓词，是 Strand 知识的高精度检索触发器：命中后唤醒目标 Strand 及其祖先，不分类 Segment、不计算工时，也不确定性归因邻近 Satellite。步不带层号，身份按读数/谓词/值的 canonical 小写形判等；Matcher 只在目标日期落入 Strand 有效范围时激活（ADR-029/030/031）。
_Avoid_: Handle / 把手、Segment 分类规则、工时归属规则、用通用工具补召回、直接拿 IdentityKey 指代

**Anchor / Satellite（锚点 / 卫星）**:
策展纪律词汇，**非机制**（ADR-029 降级）：特异性标识（锚点）才进 Strand 指纹；通用工具（卫星：blender / AE / 浏览器）不进指纹、写进自由释义（"做这个项目时通常开着 AE"），归因在叙事时由 LLM 对着时间线 + 释义完成——语义时效性（同一工具先后服务不同项目）由此消解。无强度推断代码、无角色存储字段。
_Avoid_: 当作实体 / 存储字段；把 Satellite 当 Strand 的定义性证据

**Mute（静音）**:
对一个 Matcher 的负向裁决——"这个观测不承载知识，别再就它发问、也别试图绑定 Strand"。与"绑定到 Strand"是裁决的两个出口，同住知识库。只作用于知识 / 发问层，**不碰 Recap**（被静音的观测照样如实进叙事，无损原则）。
_Avoid_: 墓碑 / Tombstone / Adjudication（设计期黑话，已弃用）、Hide（Mute 不从 Recap 隐藏）

**Asking（发问）**:
与叙事吃同一 digest 的教学入口：先展示真实 ActivityCluster 的大概时段与跨 Source 证据，让用户用自然语言补充私有含义；再由 LLM 整理成可编辑的 Strand/Episode/Matcher/Probe 变更集，用户确认后确定性提交。偏安静、每日封顶；不基于 Recap 散文自动发问，但允许用户从 Recap 主动发起纠正（ADR-029/031）。
_Avoid_: 分诊 / Triage、从散文自动反推事实、LLM 静默写知识、把结构化提案当用户确认

**Validation Policy**:
SegmentValidationPolicy 是 Collection 与 Analytics 共用的纯段完整性判定：拒绝未来时间戳、非法区间、
缺失核心身份等畸形数据，不修复输入。Analytics strict ingest 对任一不合法项返回整批 `422`，且不创建
ActivitySegment 或 provisional App；既有 Segment Id 的 Device / Source / IdentityKey 冲突同样整批拒绝。
合法 duplicate、乱序 snapshot 与批内同 Id 单调扩展仍按 Snapshot Upsert 幂等收敛。
