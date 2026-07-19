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
segments → LLM 输入的确定性压缩（纯函数，可单测）：system 段按设备分轨作注意力骨架（轨内互斥、带时长），插件段按 IdentityKey 聚合作语义细节轨；碎段合并/丢弃只影响投影不动数据。未来外部 Agent/MCP 能力暴露的开门处（不预建，ADR-023 §2）。
_Avoid_: 复刻标签升级喂单线（ADR-019 是展示层且有损，被 ADR-023 §3 否决）

**Strand（脉络）**:
用户生活里一个有名字的持续活动线索（项目 / 爱好 / 一个人 / 追的剧），被 Recap 用来把叙事从"开了哪些 App"升级为"在做什么"。形状 = 名字 + 自由释义 + 一组成员 Handle（它的可观测指纹）；单 Handle 是退化形态（等价一条实体释义，如"花生 = 敏毕设"）。**策展层，非派生物**——名字与含义只存在于用户脑中，segments 只提供证据、AI 提供猜想、用户确认成事实。per-Owner，独立存储，按值引用 Handle，**绝不写回 segment**（无损原则，ADR-012/017）。
_Avoid_: Project（太窄，排除爱好 / 人 / 剧）、Tag、Note

**Handle（把手）**:
知识层的可观测身份单元：一个 (Source, token) 对，token 取该 Source 最自然的**粗**身份（browser→domain、system→AppName、vscode→仓库根）。是 segment 上 IdentityKey 的粗化 / 派生，**不是 IdentityKey 本身**——IdentityKey 是采集器"同一活动"判据（可细到 origin+path），Handle 是知识层选定的挂载粒度。Strand 靠它认出自己；反哺时靠它把当天 segment 归给某个 Strand。
_Avoid_: 直接拿 IdentityKey 指代（粒度不同）

**Anchor / Satellite（锚点 / 卫星）**:
Handle 的强度角色，从"摊布特异性"推断（冷启动用 Source / 类型作弱先验）：近乎只与一个 Strand 共现的是 **Anchor**（强，看到即认出 Strand）；摊在多个 Strand / 多天里的通用工具（blender / AE / 浏览器）是 **Satellite**（单独无身份，逐日跟随在场 Anchor 归属）。语义时效性（同一工具先后服务不同项目）由此吸收——Satellite 跟锚点走，无需时间窗记账。
_Avoid_: 把 Satellite 当 Strand 的定义性证据

**Mute（静音）**:
对一个锚点 Handle 的负向裁决——"这把手不承载知识，别再就它发问、也别试图绑定 Strand"。与"把手绑定到 Strand"是裁决一个把手的两个出口，同住知识库。单位是锚点 Handle（非易逝的簇）；只作用于知识 / 提问层，**不碰 Recap**（被静音的把手照样如实进叙事，无损原则）。
_Avoid_: 墓碑 / Tombstone / Adjudication（设计期黑话，已弃用）、Hide（Mute 不从 Recap 隐藏）

**Validation Policy**:
摄入门卫（SegmentValidationPolicy / UsageValidationPolicy）：拒收未来时间戳、非法区间等畸形数据。拒收即丢弃，不修复——采集端负责数据正确性。
