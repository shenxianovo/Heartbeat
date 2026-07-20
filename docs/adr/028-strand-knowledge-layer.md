# ADR-028: Strand——Recap 之上的人机共建知识层

## Status: Superseded in part by [ADR-029](./029-observation-depth-matcher.md)

> §3（Handle 粗粒度、Anchor/Satellite 机制）、§4（粗筛 + LLM 分诊选题）作废；
> §6 的 digest 形状被修订（深度树）。§1/§2/§5/§7/§8 存活并被 ADR-029 继承。

## Date: 2026-07-19

> 设计定于 2026-07-19 grilling session（grill-with-docs）。实现待动工。

## Context

Recap（ADR-023）能把一天的 segments 讲成散文，但它只认得机器观测到的**表层把手**——`AppName`、`domain`、`IdentityKey`、窗口标题。它讲得出"你开了 msedge（localhost）、vscode（hyperframes-workspace）、blender、AfterFX"，讲不出"你在做 HyperFrames 动效预研"。因为**"你在做什么"这层含义不存在于 segments 里，存在于用户脑中**：`hyperframes-workspace` 是什么项目、`花生` 指代什么、`localhost:5173` 跑的是哪个东西——这些私有语义机器无从推断。

愿景是"x 年前的今天我在做什么"，回答的主语正是**项目/活动**，而非应用清单。所以需要一层机制，把用户脑中的私有语义**捞出来、确认、入库**，反哺 Recap，让叙事从"开了哪些 app"升级为"在做什么"。

一次需定清的张力：

1. **知识的原子单位**：一个 3D 动效项目表现为 `repo + localhost app + blender + AE` 的**共现簇**，不是任一单独 token。给"blender = 3D 软件"加注释既无价值（模型本就知道）也永远拼不出那件事的意义——意义是**跨把手涌现**的。
2. **语义时效性**：同一工具会漂移——AE 这个月服务花生，下个月服务别的项目。任何"AE 属于 X"的永久断言都会过期。
3. **与 Recap 缓存契约的冲突**：Recap 是纯派生物、无主动失效（ADR-023 §4）。但新知识意味着**过去**的 recap 也该retroactively受益（"命名 HyperFrames 后，去年的 HyperFrames 日子也该可读"），这戳破了"历史永不过期"。
4. **交互复杂度**：确认回路天然想做成多轮对话，但那会引入有状态、交互式的 LLM agent，量级远超 ADR-023 的一次性 recap。

## Decision

### 1. 定位：手段先行，第二大脑留门

知识库首先是**喂 Recap 的个性化输入**，不是独立可浏览的第二大脑。第一版做成"AI 发问 → 用户确认 → 反哺 recap"的最短闭环。数据模型不焊死，将来真有"翻查我的知识"的困扰时再长出浏览/检索层（CONTEXT-MAP 定位不变量："不预建，但门开着"）。否决一上来就建可查询第二大脑——那是为未来预建成本。

### 2. 核心对象 Strand（脉络）

一个 **Strand** = 名字 + 自由释义 + 一组成员 **Handle**（它的可观测指纹）。单 Handle 是退化形态，等价于一条实体释义（"花生 = B 站实习时部门做的产品"）——实体释义与项目理解是**同一个类型**，不搞两套系统。

Strand 是**策展层，非派生物**：segments 提供证据、AI 提供猜想、用户确认成事实。per-Owner、独立存储、按值引用 Handle，**绝不写回 segment**（无损原则，ADR-012/017）。

### 3. Handle 的粗粒度与 Anchor/Satellite 强度

**Handle** = 知识层的可观测身份单元，(Source, token) 对，token 取该 Source 最自然的**粗**身份（browser→domain、system→AppName、vscode→仓库根）。是 IdentityKey 的粗化/派生，不是 IdentityKey 本身。粗粒度贴合"一个实体 = 一个站 + 一个 app"的心智，最小化反复发问。共享宿主（github.com 之类）domain 粒度失效——与 browser IdentityKey 覆写表是同一问题的两面，**不预建路径级拆分**，被咬时再用同套机制解。

Handle 有**强度角色**，从"摊布特异性"推断（冷启动用 Source/类型作弱先验）：

- **Anchor（锚点）**：近乎只与一个 Strand 共现，看到即认出 Strand。
- **Satellite（卫星）**：摊在多个 Strand/多天里的通用工具（blender/AE/浏览器），单独无身份，**逐日跟随在场 Anchor 归属**。

**语义时效性由此吸收，无需时间窗记账**：AE 是 Satellite，它跟着当天在场的锚点走，没有"AE 属于花生"的永久断言需要维护。残余情况（一个 Anchor 被真正改用）罕见，发生时对不上老证据、AI 自然重问——真被咬再上 `validFrom/validTo`（不预建）。

**自锚优先**：已被裁决的把手（绑定为某 Strand 的锚点，或已 Mute）**退出跟车逻辑**——摊布指标再像 Satellite 也不随当天在场的其他锚点归属。交错日（打游戏 ↔ 做项目来回切前台）因此各归各：两个 Strand 同日在场，注意力线段级互斥（ADR-017）保证时长不双计。游戏与 AE 的区别正在于此：AE 是服务别的锚点的工具，游戏是它自己那件事的锚点。

### 4. 提问器 = 投影的第三出口

提问器复用 ADR-023 §2 留好的"同一投影加新出口"seam：

```
                 ┌──────────── Strand 库（策展层）────────────┐
                 │ 已知释义注入                                │ 绑定/Mute
                 ▼                                            │
segments ──▶ 投影（确定性，可测）──┬─▶ ① prompt+LLM ─▶ recap 叙事 │
                                 └─▶ ③ 提问器 ─▶ 提案 ─▶ 用户裁决 ─┘
```

提问器输入 = 当日投影（把手、时长）+ Strand 库，本质是一次 **diff**：今天的把手减去已被 Strand 或 Mute 裁决的部分 = 疑惑。**不根据 recap 叙事发问**——散文是有损派生物、随时可弃，在其上建知识 = 在缓存上盖楼。

**提问单元 = 单个把手，选题靠 LLM 分诊而非确定性打分（2026-07-19 二次修正）。** 演化经过两次证伪：

1. 初版把一天的共现把手聚成 constellation、问"这簇是一件事吗"——被真实数据证伪：真实的一天没有空闲缝可断会话（explorer / 微信 / 浏览器每几分钟前台交错），聚簇塌成一个 40 把手的巨簇，喂 LLM 得到"武汉大学学生的一天"这种零信息量提案。改为单把手提问。
2. 单把手后用 `特异性 × 时长` 打分选题——又被证伪：它把 bilibili、github、msedge 顶到最前，而这些恰恰是 **LLM 自己就认识、plain recap 早已消化**、最不该问的东西。错的是判据本身：不该是"什么又 specific 又久"，而是"**什么是 AI 解释不了的**"——这是个世界知识判断，确定性投影层结构上做不到，必须交给 LLM。

**定稿：确定性层做粗筛限流，LLM 做分诊选题。**

- **确定性粗筛**（控 LLM 调用量，非选题）：哨兵剔除（explorer、ShellExperienceHost、__away__、newtab、localhost、**以及浏览器进程 msedge/chrome/firefox——它们是卫星工具，不是"你在做的事"，且与 browser 域名把手重复计数**）；噪声地板 <60s；ubiquity 压制（14 天回看里天天出现的把手，如微信 / QQ，抬高时长门槛、排名压后）。粗筛后剩一个短名单。
- **LLM 分诊**（选题内核）：对短名单每个把手判三态——
  - **认识 + 有把握**（bilibili / github）→ **不问**，gloss 直接进 recap 的"已知脉络"块。
  - **不认识 / 像私有冷门**（huasheng.cn、陌生 .exe）→ 进**提问队列**。
  - **不认识、也没把握** → **既不问、也不 gloss**（用户定调"偏安静：宁可不问"）；recap 留原始把手，不自作主张。
- **裁决日志当锚（免标注二元信号）**：分诊 prompt 塞几条用户历史 bind（值得问的样子）/ mute（别问的样子）作 few-shot。日 1 空 log → 退化成纯世界知识裸判——**这就是冷启动的粗跑，不是单独分支**。裁决攒起来 → 越来越贴用户口味（mute 一个视频站泛化到下一个）。这是"反馈多了 recap 变好"的载体：系统未来行为是**裁决日志的函数**，不是手工常量的函数。
- **封顶** 每天最多 3 个进队列，避免刷屏。
- **代价**：分诊每个短名单把手一次 LLM 调用，须与 recap 同样缓存（按天、跟水位），否则刷看板烧 token。偏安静意味着分诊自信猜错私有站时不问、recap 可能已 gloss 错——留事后"纠正/建 Strand"入口，不预建。

### 5. 确认走表单，确定性两端夹薄 LLM

裁决走**表单式**：AI 对被问的把手一次性给结构化提案卡（猜名字、猜释义），用户直接改字段、打勾提交（成员即该锚点把手；共现提示只读）。"纠错"= 编辑字段，不是聊天。

保住 ADR-023 的对称：**确定性两端（提问 diff + 提交写库，可单测）夹一层薄 LLM（出提案，不可测但薄）**。多轮对话式纠错**留门不预建**——真碰到需来回澄清再长"展开讨论"入口。

**无会话态持久化**：因为提问器是无状态 diff，一场没答完的确认，那个把手还没被裁决，下次 diff 自然再端上来——diff 本身就是"续上"机制。

### 6. 反哺注入投影层；缓存读时判脏、不自动重生成

- **注入点 = 投影层**：投影搭骨架时把当日观测到的把手解析成 Strand（HandleDerivation 确定性），digest 追加"已知脉络"块（"这些把手 = HyperFrames（释义…）"），prompt 渲染名字。解析留在可测那一半。
- **staleness 两个独立来源、两套策略**：
  - **段水位**（今天）：落后 >1h 自动重生成——ADR-023 原行为**不变**。
  - **Strand 变更**（任何日）：**只判脏、只提示、永不自动重生成**。读某天时若发现覆盖它把手的 Strand 晚于该 recap 的 `GeneratedAt`，挂"知识已更新，重新生成？"提示，用户手动点。
- 检测仅 `recap.GeneratedAt` vs 相关 Strand `UpdatedAt` 一次比较，**零失效写、零扇出**——不违反 ADR-023"无主动失效"。否决主动扇出失效（推翻核心缓存决策）。

### 7. 隐私：纯继承出境 trade-off

纯继承 ADR-023/ADR-012 的单用户出境 trade-off（"用户 == 数据主人 == 部署者自己选择出境"）。提案 LLM 调用送的把手簇+标题与 recap 同类数据，无升级。**一个新面显式记下**：用户亲手写的释义比机器观测的原始串更私密、更具主观诠释性，且会在未来每条碰到这些把手的 recap prompt 里**反复出境**。不给释义特殊待遇——它恰是让 recap 变聪明的燃料，扣在本地等于阉掉功能。多用户化时此面需连同 ADR-023 ⚠️ 一并重审。

### 8. 裁决两出口：绑定 Strand / Mute

对一个把手开口，结果只有两种：**绑定**到 Strand（正向），或 **Mute（静音）**（负向："这把手不承载知识，别再问、别绑定"）。从 diff 视角二者等价（已裁决，别再端上来）。

- Mute 单位是**锚点 Handle**（非易逝的簇——静音多把手簇无法泛化）。
- Mute **只作用于知识/提问层，不碰 Recap**（被静音的把手照样如实进叙事）。
- 引入 Anchor/Satellite 后大量噪声自动沉底，真需 Mute 的只剩"看着像锚点、复现且占真时间、但你不想立项"的东西（如天天泡的新闻站）。
- 一张表加判别位还是两张表——留实现，概念上 Strand 绑定与 Mute 是裁决一个把手的两个出口，同住知识库。

否决"墓碑/Adjudication"独立忽略清单与 Strand 分家——统一成"裁决一个把手"更简。

## Consequences

- ✅ Recap 叙事主语从 app 名升级为项目名，兑现"x 年前的今天我在做什么"。
- ✅ 复用 ADR-023 §2 投影出口 seam 与噪声纪律，不发明第二套机制；确定性两端可单测。
- ✅ Anchor/Satellite 让语义时效性、噪声沉底、冷启动先验一并解决，无时间窗记账。
- ✅ 读时判脏不写失效，守住 ADR-023"无主动失效"；token 花费全在用户手上。
- ⚠️ **Dashboard 不再只读**：多一条 `Dashboard → Analytics` 知识裁决写路径（CONTEXT-MAP 已认下）。
- ⚠️ 主观诠释持续出境（见 §7），多用户化时需重审。
- ⚠️ Handle 粗粒度对共享宿主失效，路径级拆分留待被咬（与 browser IdentityKey 覆写表同源）。
- ⚠️ 提案质量依赖 LLM 对簇的一次性猜测；表单编辑成本兜底，但猜得差时用户负担上升——对话式纠错入口留门。

## References

- [ADR-023](./023-recap-cloud-llm-projection.md) —— Recap 投影/生成分层、缓存契约、出境 trade-off、§2 Agent/MCP 出口 seam（本 ADR 的三处复用点）
- [ADR-012](./012-input-event-tracking.md) —— 出境 trade-off 同格式先例
- [ADR-017](./017-activity-segment-pluggable-collectors.md) —— Source/IdentityKey/双轨数据模型
- [ADR-019](./019-replay-attention-line-label-upgrade.md) —— 展示层标签升级（Strand 不复刻，走投影注入）
- `server/CONTEXT.md` —— Strand / Handle / Anchor-Satellite / Mute 词条
- `CONTEXT-MAP.md` —— `Dashboard → Analytics` 知识裁决写路径
- 实现：待动工（`.scratch/` 建 issue 拆片）
