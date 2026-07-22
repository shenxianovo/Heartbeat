# ADR-030: 采集器声明的观测深度表——运行时注册,声明驱动知识层

## Status: Accepted（implements ADR-029 §2 的"采集器契约声明"；amends §2 browser 深度表例子与 §3 Matcher 步的 Layer 语义）

## Date: 2026-07-22

> 设计定于 2026-07-22 grilling session（候选②）。实现待动工，issue 拆片见
> `.scratch/collector-depth-declaration/issues/`。

## Context

ADR-029 §2 宣告"每个采集器在自身契约里声明一张有序观测深度表……新采集器带自己的
深度表来插，知识层零改动"。落地四个 commit 后核查：**契约没有载体**。深度表以五种
形态散布——`DepthReadings.cs` 的 if/else（自称"server 侧镜像"，但采集器端不存在
原件，镜像即真身）、判官 SystemPrompt 里的读数词汇、`DigestAssembler.RecurringLabel`
的 per-Source 分支、`RecapProjection` 按 `AppName`/`Title` 的平行手写树、前端
`READING_LABEL` 字典。采集器注册通道（ADR-026）只传 source + flushPeriodMs；
hub → server 无任何采集器元数据通道；browser 采集器（TS）在一切共享契约之外靠注释
手工对齐。

已被咬的具体处：browser 轨在 digest 里按 url 平铺无分解，**tab_title 不进 digest
——判官"看着分解证据提案细档"的机制对 tab_title 结构性失灵**；接入 vscode 需改
server/前端至少 3-4 处。

备选路径的否决过程：**收进 shared kernel**——跨语言不可达（browser/vscode 是 TS），
恰好点名要修的采集器仍无家；**构建时嵌入**（契约文件随采集器目录、server csproj
link）——改采集器还要改嵌入，被用户否；**segment 自描述 readings**（上传时携带）——
丧失回溯性（历史段永缺，或需一次性重写事实表）、硬列与 readings 双写同值可漂移、
慢变契约混进高频事实流，三条第一性反对。定稿：**运行时注册上报**。

## Decision

### 1. 深度表 = 采集器上报的纯数据声明

```
{ source, version, collectorVersion,
  layers: [ { readings: [ { name, from, label } ] } ] }   // layers 浅 → 深，有序
```

- `version` 是**契约版本**（int，深度表变更才递增，驱动生效规则）；`collectorVersion`
  是软件版本（string，纯诊断）。
- 读数 `name` 由采集器全权命名（术语主权），**source 内唯一**（声明校验规则）；
  `label` 是人话展示名，同属采集器主权。
- 层内 `readings` 是数组（留门），解释器约定**首读数为分解轴**；当前三张表全部
  单读数，真出现伴随读数再加显式轴标记（不预建）。

### 2. 槽位映射：服务端的"反射式无关层"

`from ∈ { appName, title, identityKey, attributes.<path> }`——这是**运输层词汇**
（值放在哪），不是语义词汇（值是什么）。服务端解释器只会一个动作：按槽取值。
per-Source 代码归零，服务端永远不认识 app / url / site 这些词。

- **新读数一律走 `attributes.*` 槽**（jsonb 本就是"各 source 自由结构"）：wire 契约
  永不再改，采集器加深度 = 写 attributes + 声明加行 + version+1。
- **读时取值 ⇒ 回溯性免费**：声明只说"值可能在这个槽"，不会无中生有——历史
  segments 被新声明自动覆盖，旧版采集器的段在新声明下优雅退化（缺槽即不分解）。
  这也是否决 per-Device 声明的理由：最新版解释旧段无害，按段追溯出生版本精确性
  无收益。
- **缺读数段的退化规则**：段挂到它拥有的**最深可用读数**上，不造假值。规则通用，
  无 per-source 知识。

### 3. 传输：注册通道扩展

- collector → hub：loopback 新动词 `POST /v1/collectors/{source}/declaration`
  （body = 声明 JSON）；system 采集器住 hub 进程内，直接进程内声明。
- hub registry 存声明；hub → server 新上行调用 `POST /api/v1/collectors/declarations`
  （启动时 + 声明变更时，幂等）。
- 声明与数据流分离：segments 上传路径零改动。

### 4. 服务端声明表与生效规则

`CollectorDeclarations`：`UNIQUE (Source, Version)`，PayloadJson，ReportedAt。

- **生效表 = 每 source 取 max(Version)**。同 (Source, Version) 重报幂等覆盖
  （开发迭代友好）。
- **全局而非 per-Owner**：声明是采集器软件的契约，不是用户数据。多用户化时生效表
  是共享信任面（恶意采集器可上报垃圾声明污染他人 digest）——按定位不变量 1 记入
  trade-off，留门 = 加 OwnerId 列，不预建。
- 迁移**预插种子行**：system v1（app → title）、browser v1（url → tab_title），
  切换日行为零断层；采集器上线后上报同内容幂等收敛。
- 未声明的新 source 走**通用回落**（L1 identity ← identityKey，L2 title ← title），
  digest 不死，树浅。

### 5. 值空间层级按需提拔为深度层

单个读数的值空间内部自有层级（域后缀：shenxianovo.com ⊃ blog.shenxianovo.com；
路径前缀：/a ⊃ /a/b）。原则维持 ADR-029："同一读数上的粗细由**谓词**表达，不是
深度"——但 digest 侧的粗档证据需要聚合可见时，**由采集器把某档粒度提拔为独立
读数层**（声明 version+1，服务端零改动）。否决服务端在树内折叠 url——无关层刚建
就要长 url 解析器，白建。

- **browser v2 = site → url → tab_title**：site = 可注册域（eTLD+1 近似，www 折叠
  进主站，采集器 normalize 层负责），写 `attributes.site`。判官粗档提案由此锚定
  `(site equals ...)`，细化才下探 url prefix / tab_title contains。
- **tab_title 从 L1 挪到 L2 以下（修正 ADR-029 §2 例子）**：同一 url 下先后多个
  标题就是"该读数在下一深度的分解"的定义本身，与 system 的 app → title 同构。
  同层双读数需要轴标记 + 伴随呈现（刚退役的抽样借尸还魂）两套新机制，下层化零机制。
  ADR-029 写"同深双读数"时树未实现，实现咬了设计文本。

### 6. Layer 退出 Matcher 身份与匹配

读数名 source 内唯一 ⇒ `(tab_title contains X)` 不带层号亦无歧义。Layer 从
MatcherStepDto **移除**：Eval 按 (source, reading name) 匹配，canonical 身份 =
(reading, op, value)。深度属于声明（展示 / 隐私轴属性），不属于 Matcher——
**深度重排、插层、提拔永不失效存量 Matcher**（配合 §5 的伸缩方式，此条近乎必选，
否则每次提拔都是一次存量迁移）。存量 StepsJson 由 backfill 剥 Layer + 撞身份去重
（延续 ADR 附带 fix 的 KnowledgeIdentityBackfill 机制）。判官输出 schema 同步去层。

### 7. 消费点全部改声明驱动

DepthReadings → 通用解释器（声明 + 槽位取值 + 回落）；RecapProjection 深度树泛化
（时间骨架轨/块不动，块内分解与插件轨按声明递归，预算剪枝复用）；判官 SystemPrompt
的读数词汇段从生效声明生成；RecurringLabel = 首层读数值；前端标签 = 声明 `label`
随 questions 响应下发（`readingLabels` 字典），未知读数回落原名。四份硬编码词汇表
全部退役。

## Consequences

- ✅ "来插即用零改动"从散文变机制：新采集器 = 声明 + POST，服务端零改动。
- ✅ 历史 segments 免费获得新深度（读时取值）；"x 年前的今天"的老数据长出树。
- ✅ 判官获得站点/标题证据：粗档锚 site、细档下探——tab_title 细化从结构性不可能
  变为常规路径。
- ✅ 深度重排免费（Layer 退出身份）；采集器术语与展示名主权完整。
- ⚠️ 生效表全局 max(Version)：多用户化时声明是共享信任面，留门 per-Owner 列。
- ⚠️ 声明未达且无种子的新 source 走通用回落，树浅（可接受：数据不丢，树后到）。
- ⚠️ eTLD+1 近似（public suffix 名单）是 browser 采集器侧的持续维护点。
- ⚠️ MatcherDto 破坏性变更（去 Layer）：NSwag / 判官 schema / 存量 StepsJson 同步
  迁移；ADR-029 §2 例子与 §3 Matcher 形状自本 ADR 起以此为准。

## References

- [ADR-029](./029-observation-depth-matcher.md) —— 被实现的 §2 与被修正的
  §2 例子 / §3 Layer 语义
- [ADR-026](./026-collector-registry-deactivation.md) —— 被扩展的注册通道
- [ADR-017](./017-activity-segment-pluggable-collectors.md) —— 观测深度 = 隐私轴，
  分层可拆
- `server/CONTEXT.md` —— Observation Depth / Matcher 词条（本 ADR 同步更新）
- 实现：`.scratch/collector-depth-declaration/issues/` 01-04
