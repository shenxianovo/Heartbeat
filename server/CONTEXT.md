# Analytics

接收采集数据，以快照 upsert 持久化，聚合报表，向 Dashboard 提供只读 API。

## Language

**Ingest（摄入）**:
统一摄入例程（ADR-018）：校验 → App 关联 → 按 Id 快照 upsert。摄入可交换、可重入——乱序重传与批内同 Id 快照收敛到同一行。上传入口收敛为 `/segments` 单条（ADR-020）：system 段由 Agent 客户端算好 IdentityKey 经同一入口上传；`POST /usage` 及其映射层已退役，`GET /usage` 仅存查询投影。
_Avoid_: Merge、续接（ADR-001 的服务端合并已被 ADR-018 快照 upsert 取代；CanMerge 一词只存在于历史 ADR 中）

**Snapshot Upsert（快照 upsert）**:
Id 即活动身份：已有行则单调生长（EndTime 取 max、attributes 后写胜），新 Id 插入。采集端对同一活动多次上报的是同一 Id 的更大快照，不是新段。

**Report（报表）**:
对某 Owner 某时间窗的聚合视图（daily / weekly）。只消费 system 段——互斥轨，时长可求和；插件段只进回放，不进统计（ADR-017 §4 统计边界）。跨窗段做区间重叠 + 裁剪：只计落在窗口内的部分，不漏不双计（ADR-018 §4）。
_Avoid_: Statistics, Summary

**App**:
归一化的应用记录（进程名唯一），摄入时由 AppName 提示关联或创建。AppIcon 由 Agent 单独上报，挂在 App 下。App 是统计聚合的主维度。

**Owner / Device**:
数据隔离的两级键：所有查询以 `Device.OwnerId` 过滤（多用户就绪，见 CONTEXT-MAP 定位不变量 2）；Device 是一台采集来源机器，报表可按 Device 过滤或跨设备聚合。

**User Provisioning（用户供给）**:
懒建，由**本人首次带 JWT 的请求**触发：upsert User 行（`Id = sub`，`Username = preferred_username`，默认 private）。匿名按用户名读取只查本地 Users 表，查不到即 404——不回源 Auth 平台、不建行（防爬虫刷空行 + 用户名枚举）。**sub-first 规则**：带 JWT 请求一律用 `sub` 定位 User 行，Username 只是可刷新的显示缓存 + 匿名查询入口。username 可变（AuthService 改名立即释放旧名，GitHub 模式）：供给回写含**驱逐**——同名异 sub 的 stale 行被改为 `~{sub}` 占位（`~` 不在上游字符集，永不撞真名），被驱逐者下次带 JWT 请求自愈。设计定于 2026-07-17（ADR-027），实现进度见 `.scratch/multi-user/issues/01-username-rename-landmine.md`。
_Avoid_: 注册/Registration（Heartbeat 无注册概念，账号归 Auth 平台；显式注册流程留给未来隐私条款同意场景）

**Dashboard Visibility（看板可见性）**:
每个 User 一个 `IsPublic` 开关，默认 private。private 时按用户名的匿名读路径一律 404（不泄露用户名存在性）；本人经 JWT（`sub == User.Id`）读自己的数据，不受开关影响。当前阶段 public = 全保真放行现有读端点（决策：先 A 后 C——待自定义看板落地后升级为每卡片可见性配置，全保真公开届时退役）。
_Avoid_: Public Profile（GitHub 语义是粗聚合展示，这里 public 是全量数据，语义不同不要混用）

**Recap（叙事摘要）**:
对某 Owner 某日窗口的 LLM 叙事视图（ADR-023）：投影 → 云端 LLM → 落库缓存。纯派生物——segments 是事实，Recap 随时可重生成，故无主动失效：历史日期永不过期，今天按水位（生成时消费到的最新 segment 时间，落后 >1h 重生成）+ 显式重生成入口。跨设备聚合，无 deviceId 维度。口吻是日记/档案：只叙事，不评判不打分不建议。空日不调 LLM，失败不写缓存。
_Avoid_: Summary（Report 词条同禁）、日报（汇报工具的词，Recap 是记忆）

**Recap Projection（Recap 投影）**:
segments → LLM 输入的确定性压缩（纯函数，可单测）：system 段按设备分轨作注意力骨架（轨内互斥、带时长），插件段按 IdentityKey 聚合作语义细节轨；碎段合并/丢弃只影响投影不动数据。digest 的身份维度按观测深度长成深度树（块内下一深度分解、预算剪枝，ADR-029），叙事与发问两次调用共用同一 digest。未来外部 Agent/MCP 能力暴露的开门处（不预建，ADR-023 §2）。
_Avoid_: 复刻标签升级喂单线（ADR-019 是展示层且有损，被 ADR-023 §3 否决）

**Strand（脉络）**:
用户生活里一个有名字的持续活动线索（项目 / 爱好 / 一个人 / 追的剧），被 Recap 用来把叙事从"开了哪些 App"升级为"在做什么"。形状 = 名字 + 自由释义 + 指纹（一组 Matcher）；单 Matcher 是退化形态（等价一条实体释义，如"花生 = B 站实习时部门做的产品"）。**策展层，非派生物**——库里只存用户亲口确认的事实（ADR-029 契约）：segments 提供证据、AI 提供猜想、用户确认成事实，机器世界知识永不入库。名字 / 释义是自由文本，绝不加 schema。per-Owner，独立存储，**绝不写回 segment**（无损原则，ADR-012/017）。注入只在指纹当日命中时发生——"过期"由在场性自动处理，零时间字段（`validFrom/validTo` 留门不预建）。**指纹靠逐日裁决归入生长**（判官每卡只锚一个 Matcher）：绑定无 Id 撞名（大小写不敏感收敛）= **归入**——成员并集追加、释义空则补位非空不动（脊柱聚合、身体策展）；整组替换只属带 Id 的编辑路径（未来编辑表单契约）。
_Avoid_: Project（太窄，排除爱好 / 人 / 剧）、Tag、Note；把问题卡的归入当"重定义"（那会碾压用户亲口确认的既有指纹与释义）

**Observation Depth（观测深度）**:
每个采集器在自身契约里声明的有序观测读数表，浅 → 深（system：进程/App → 窗口标题；browser：URL / 标签页标题 →（未来）内容摘要 → DOM；vscode 规划：仓库根 → 文件路径）。层内可有多个读数；同一读数上的粗细（domain vs 全 URL）由谓词表达，不是深度。**同时是隐私敏感度轴**——与 ADR-017"采集能力分层可拆"是同一张表。digest 的身份维度按它长成**深度树**：节点 = (读数, 并集时长)，子节点 = 下一深度分解，渲染 = 确定性预算剪枝（展开门槛、子数封顶、尾部折叠）。
_Avoid_: 粒度（粗细是谓词维度，不是深度）；在知识层写死采集器字段名（app / url / domain 归各采集器契约）

**Matcher（匹配子）**:
知识层的指纹原子：沿某 Source 深度树的**路径谓词**——各层 (读数, 谓词, 值) 的合取，谓词 ∈ {等于, 前缀, 包含}，单层是退化形态。例：`(system, L1 app = Code) ∧ (L2 title contains "hyperframes")`。Strand 指纹 = Matcher 集合；Mute 的单位也是 Matcher。digest 是深度树的观测投影，Matcher 是同一棵树上的路径谓词——发问 LLM 看着前者提案后者，粗档默认、细档只在分解证据要求时提案。**裁决身份 = canonical 小写形**（Source / 读数 / 谓词 / 值 trim + 小写、步骤排序去重后的确定性 JSON）：唯一索引、Mute 幂等、读时 diff 皆按它判等，与命中语义（MatcherEval 大小写不敏感）是同一把尺子——身份等价类 ≠ 命中等价类时，"别再问"对着字符串承诺、对着观测事实食言。
_Avoid_: Handle / 把手（ADR-028 固定粗粒度 (Source, token)，ADR-029 起退役）、直接拿 IdentityKey 指代（IdentityKey 是采集器"同一活动"判据，不是知识层挂载点）、身份与命中用两把尺子（大小写变体裂身份）

**Anchor / Satellite（锚点 / 卫星）**:
策展纪律词汇，**非机制**（ADR-029 降级）：特异性标识（锚点）才进 Strand 指纹；通用工具（卫星：blender / AE / 浏览器）不进指纹、写进自由释义（"做这个项目时通常开着 AE"），归因在叙事时由 LLM 对着时间线 + 释义完成——语义时效性（同一工具先后服务不同项目）由此消解。无强度推断代码、无角色存储字段。
_Avoid_: 当作实体 / 存储字段；把 Satellite 当 Strand 的定义性证据

**Mute（静音）**:
对一个 Matcher 的负向裁决——"这个观测不承载知识，别再就它发问、也别试图绑定 Strand"。与"绑定到 Strand"是裁决的两个出口，同住知识库。只作用于知识 / 发问层，**不碰 Recap**（被静音的观测照样如实进叙事，无损原则）。
_Avoid_: 墓碑 / Tombstone / Adjudication（设计期黑话，已弃用）、Hide（Mute 不从 Recap 隐藏）

**Asking（发问）**:
与叙事吃**同一个 digest** 的第二次独立 LLM 调用（digest 作共享 prompt 前缀吃 provider 缓存）。判断"什么是世界知识解释不了的"整体交给 LLM——确定性层只供证据（digest 深度树 + 近 14 天高频注释 + 已裁决标注）与裁剪（每日 ≤3、对已裁决 Matcher 的 diff）。prompt 附 few-shot 裁决日志（空日志 = 冷启动裸判）；偏安静：宁可不问。不基于 recap 散文发问（有损派生物上不盖楼）。缓存按天 + 水位、失败不写；裁决后对缓存问题做确定性 diff 过滤，零 LLM 重调。产出 = 问题卡（Matcher 提案 + 时段 + 一次性名字/释义提案），走表单确认。
_Avoid_: 分诊 / Triage（ADR-028 §4 的 per-handle 机制，已拆除）、提问器打分选题（两次被证伪的确定性选题）、对话式确认（留门不预建）

**Validation Policy**:
摄入门卫（SegmentValidationPolicy / UsageValidationPolicy）：拒收未来时间戳、非法区间等畸形数据。拒收即丢弃，不修复——采集端负责数据正确性。
