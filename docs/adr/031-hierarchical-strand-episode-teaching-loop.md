# ADR-031: 层级 Strand、Episode 与统一教学闭环

## Status: Accepted

## Date: 2026-07-24

> 设计定于 2026-07-24 grill-with-docs session。它扩展 ADR-028/029 的 Strand
> 知识层，修订 ADR-029 §1 的“时间语义零字段”、ADR-028/029 的单阶段表单确认，
> 并修订 ADR-023 §4 的“历史 Recap 永不过期”。

## Context

Recap 已能看到完整的数字活动证据：VS Code 的 `hyperframes-workspace`、浏览器里的
`Hyperframes Studio`、期间发生的 ChatGPT 对话，以及它们在时间线上的交错关系。但这些
观测仍回答不了用户真正想回看的问题：这是一段“哔哩哔哩实习”中的 Hyperframes 产品
调研与可行性分析，而且这条脉络可能持续数天或数周。

ADR-028/029 建立了用户确认的 Strand 与 Matcher，但当前 Strand 是扁平的，无法表达
“实习 → 项目 → 工作脉络”；Matcher 也容易被误读成 Segment 分类或工时归属。进一步
grilling 又揭露了一个更细的事实层：第一次出现的行为尚不能判断是持续 Strand 还是一次
临时发生。如果把两者都塞进 Strand，树会退化成任务日志；如果只临时喂给一次 Recap，
用户纠正又会在未来重生成时丢失。

同时，主动发问、纠正错误 Recap、手动管理知识是同一件“用户教 Heartbeat 私有语义”的
三个入口，不应长成三套互不相通的记忆。知识变化还会改变历史 Recap 所需的上下文，
“历史永不过期”不再成立，但也不能批量重生成而浪费 LLM token。

## Decision

### 1. 只服务叙事的四层模型

知识层第一版仍只服务 Recap 叙事，不承担精确工时、项目管理或活动台账：

```text
Observation
    ↓ 临时聚合
ActivityCluster
    ↓ 用户教学
Episode ─────────→ Strand Tree
    ↓                    ↓
    └──────── Recap ←────┘
```

- **Observation** 是 Collector 的 source-native 读数，是事实来源。
- **ActivityCluster** 是模型从时间线临时推导的证据视图，只为发问服务，不持久化。
- **Episode** 是用户确认的、有大概时间边界的一次具体发生。
- **Strand** 是跨日期延续的私人叙事脉络。
- **Recap** 将当日 Observation、时间关系、Episode 与相关 Strand 知识组合成叙事。

临时日内推断若未被用户确认，不进入知识库；ActivityCluster 与 Episode 都不拥有
Segment，系统不持久化 `Segment → Strand` 或 `Segment → Episode` 关系。

Strand、Matcher、Mute、Episode、RecurrenceProbe 等知识实体的持久化 ID 统一由应用层
生成 UUIDv7；路径谓词的业务等价性仍由 canonical value 决定，不能用记录 ID 代替。

### 2. Strand 是严格单父级、带日期范围的语境树

Strand 第一版采用严格单父级树，概念形状为：

```text
Id                  UUIDv7，由应用层生成
OwnerId
ParentStrandId?     自引用
Name
NormalizedName
Gloss
StartedOn?          用户本地日期，近似
EndedOn?            用户本地日期，近似；空表示可能仍在进行
CreatedAt
UpdatedAt
Matchers[]
```

约束如下：

- 每个 Strand 至多一个父级；顶层 `ParentStrandId = null`。
- 允许任意实用深度，不给层级预设“实习 / 项目 / 任务”等含义，也不增加固定 `Kind`。
- 禁止把节点移到自身或后代下，整棵树必须无环。
- 父级可以没有 Matcher，只作为子级命中后带入的语境容器。
- 已有 Strand 的绑定、编辑、移动一律按 `StrandId`，名称只承担人类表达。
- 同 Owner、同父级、规格化名称相同的 Strand 可以在不同时期重复，但其已知有效日期
  范围不得重叠；未知端点视为向对应方向无限延伸。
- 子 Strand 的已知有效范围不得超出父 Strand 的已知范围。结束仍有活跃子节点的父级
  时，必须让用户选择一并结束、建立后继节点或先整理层级，不静默级联。

直接命中一个 Strand 时，按根到叶顺序注入它的完整祖先链；命中祖先不会激活全部后代。
例如命中“产品调研与可行性分析”会带入“哔哩哔哩实习 → Hyperframes”，只命中
“哔哩哔哩实习”则不会把所有项目塞进 prompt。

`MoveStrand` 只表示纠正过去的错误理解，因此会重写该 Strand 的历史层级解释并使相关
Recap 判脏。现实中的归属变化不移动原节点：结束旧 Strand，在新父级下创建新 ID 的
Strand。由此避免为父子关系预建时间版本边，同时保留历史语境。

### 3. Matcher 只是知识检索触发器

Matcher 的路径谓词形状继续继承 ADR-029/030，但语义收窄为：

> Matcher 命中 Observation 时，检索目标 Strand 及其祖先知识。

它不是 Segment 分类器、Episode 生成器或工时归属规则。Matcher 命中只证明当日证据足以
唤醒一条知识，不证明所有邻近 Browser、ChatGPT 或通用工具都属于它；附近 Satellite 的
关联继续由 LLM 根据完整时间线和 Gloss 叙事。

Matcher 优先高精度、允许低召回，并在目标 Recap 日期落入 Strand 有效范围时才激活。
通用工具不为补召回而进入指纹。Mute 仍是对 Matcher 的负向裁决，只抑制知识发问，
不从 Recap 删除原始观察。

### 4. Episode 是用户确认的有界发生，不是 Strand 叶节点

Episode 与 Strand 是两种不同事实，不共享树节点类型：

```text
Episode
- Id                  UUIDv7，由应用层生成
- OwnerId
- ApproximateStart?   可空、近似
- ApproximateEnd?     可空、近似
- LocalDate           至少归属一个用户本地叙事日
- Text                用户确认的当天事实
- RelatedStrandId?    最多一个最具体 Strand
- CreatedAt
- UpdatedAt
```

Episode 可以独立存在，也可以关联一个最具体 Strand，并通过该 Strand 获得祖先语境。
横切背景暂时写在 Episode 文本中，不建立多对多。确实包含两件事的证据应拆成两个
Episode。

ActivityCluster 不会自动落库为 Episode。Episode 只来自用户确认的主动教学、Recap
纠正或手动记录。已知 Strand 的 Matcher 命中也不会自动制造“今天做了 X”的 Episode；
Observation 与 Strand 已足够支撑常规叙事。

一次用户回答可以同时包含长期和当天事实，结构化确认因此可以原子提交 Strand 变更与
Episode 创建；用户可分别编辑、接受或拒绝每项变化。

### 5. RecurrenceProbe 只寻找“可能再次出现”

第一次出现时，用户可能明确它是一次性的、明确它会持续，或暂时不知道。第三种情况可在
Episode 上保存一个用户确认的 **RecurrenceProbe**：它复用 Matcher 的路径谓词表达能力，
但不复用 Matcher 的领域后果。

- `StrandMatcher` 命中：检索并注入 Strand 知识。
- `RecurrenceProbe` 命中：只通知 Asking“某个未归属 Episode 可能再次出现”。
- `MuteMatcher` 命中：抑制相似知识问题。

Probe 命中不会注入旧 Episode、自动创建 Strand、自动关联历史 Episode 或修改 Recap。
系统可以据此主动建议“提升为 Strand”，但创建/选择 Strand、关联哪些 Episode、以及将
Probe 提议为 Strand Matcher 都必须由用户确认。提升是非破坏性的：原 Episode 保留，
新增持续 Strand，并将 Episode 关联到它；不是把 Episode 行改成 Strand。

明确一次性的 Episode 不创建 Probe。Probe 被提升、否认或静音后进入已解决状态，不继续
重复发问。

### 6. 三个入口共用两阶段教学闭环

主动发问、Recap 纠正、手动管理共享同一知识写模型。核心操作包括：

```text
CreateStrand / UpdateStrand / MoveStrand / EndStrand
BindMatcher / MuteMatcher
CreateEpisode / UpdateEpisode / RelateEpisode
CreateProbe / ResolveProbe / PromoteEpisode
```

主动发问不再是“LLM 直接给最终表单”的单阶段流程，而是：

```text
真实 ActivityCluster 的证据卡
→ 用户用自然语言说明私有含义
→ LLM 整理成可编辑的结构化变更集
→ 用户确认选中的变更
→ 确定性提交端事务性写入
```

证据卡展示大概时间区间与跨 source 观察，但不把模型推断冒充事实。结构化变更应支持选择
已有 Strand（按 ID）、创建顶层或子级、编辑名称/Gloss/日期、创建 Episode、绑定
Matcher/Probe、跳过和静音。

Recap 纠正的本质也是教学：先把纠正拆成可复用 Strand 知识与日期级 Episode，再重新
生成目标日期；不直接把用户文本补丁式写进 Recap 正文。ADR-029 对“基于 Recap 散文
自动发问”的否决仍成立——纠正入口由用户主动发起，不从有损散文自动推导问题。

机器只能提议层级、名称、Gloss、时间和谓词；私有语义进入库前必须有用户确认。

### 7. Recap 按日期保存知识投影，惰性判断过期

目标日期完成教学或纠正后立即重新生成。其他历史日期不批量调用 LLM，也不使用全局知识
版本让全部 Recap 失效。

每次生成 Recap 时，除现有 SegmentWatermark 外保存该日期实际使用的**知识投影标识**。
投影至少覆盖：

- 当日直接命中的 Strand；
- 被带入的祖先链及其名称、Gloss、日期范围；
- 参与检索的 Matcher；
- 与该日重叠的用户确认 Episode 及其关联。

读取历史日期时，以当前 Observation 和知识库确定性重算该日期的投影标识。相同则直接
返回缓存；不同则返回“相关知识已更新，可以重新生成”的提示，绝不自动调用 LLM。
这能发现 Strand 编辑/移动/结束、Matcher 增删、Episode 新增/编辑/关联，以及新 Matcher
使旧日期首次相关等变化，而不需要失效写、扇出或 Segment 归属表。

ADR-023 的段水位策略仍独立存在：今天的 SegmentWatermark 落后超过阈值可以按原规则
自动重生成；知识变化只判脏、只提示，除刚被用户纠正的目标日期外均由用户控制 token。

## Rejected alternatives

- **保持扁平 Strand**：无法表达“实习 → 项目 → 工作脉络”，Recap 仍缺上位语境。
- **多父级 DAG**：第一版会把移动、有效时间、注入顺序和 UI 都升级成图问题；横切关系先
  放自由文本。
- **给 Strand 增加固定 Kind**：实习、身份、项目、目标、阶段不在同一分类维度；只有在
  类型产生真实不同行为时再引入。
- **把临时行为也改名后塞进统一 Strand/Activity 节点**：会混淆持续语境与有界发生，
  污染树、Matcher 和生命周期语义。
- **持久化模型推导的 ActivityCluster 或 Segment 归属**：把叙事辅助层变成活动台账和
  工时系统，且会将近似推断固化成事实。
- **直接编辑 Recap 正文**：只修一篇文章，不会改善未来理解，重新生成时还会丢失纠正。
- **全局知识版本或批量重生成**：无关知识变化使所有历史日期失效，或产生不可控 token
  花费。
- **现实归属变化时直接 MoveStrand**：会以当前关系覆盖历史；移动只保留给纠错。

## Consequences

- ✅ Recap 获得跨天、分层的私人语境，同时保留当天具体事实。
- ✅ 第一次出现无需过早判断；Episode/Probe 让“临时还是持续”可以随证据演化。
- ✅ 三个教学入口写入同一模型，用户纠正能改善未来叙事而非只修改一篇文章。
- ✅ 历史 Recap 精确到相关知识惰性判脏，避免全局失效和批量 token 消耗。
- ✅ Strand 树保持严格、可解释；不引入 DAG、固定类型或时间版本边。
- ⚠️ 知识模型从 Strand/Matcher 扩展到 Episode/Probe，提交协议和前端确认 UI 明显变深。
- ⚠️ 同父同名的日期范围不重叠、父子范围包含和无环约束需要确定性服务层校验。
- ⚠️ 用户自由文本、Episode 和层级 Gloss 会作为相关日期的 Recap prompt 输入，延续并
  扩大 ADR-023/028 的主观私人信息出境面；多用户化时必须重审。
- ⚠️ Probe 是新的生命周期对象；若不严格解决/静音，会重新引入重复发问噪声。

## References

- [ADR-023](./023-recap-cloud-llm-projection.md) —— Recap 投影、缓存与 token 控制；
  “历史永不过期”由本 ADR 修订为相关知识投影惰性判脏
- [ADR-028](./028-strand-knowledge-layer.md) —— 用户确认知识层与 Dashboard 写路径；
  扁平 Strand、单阶段表单及 staleness 检测由本 ADR 扩展
- [ADR-029](./029-observation-depth-matcher.md) —— 深度树、路径 Matcher、同 digest 发问；
  “时间语义零字段”和单阶段问题卡由本 ADR 修订
- [ADR-030](./030-collector-depth-declaration.md) —— Matcher 使用的 source-native 读数声明
- `server/CONTEXT.md` —— Strand / Episode / ActivityCluster / RecurrenceProbe / Asking 词条
- 实现拆片：`.scratch/hierarchical-strand-teaching/issues/`
