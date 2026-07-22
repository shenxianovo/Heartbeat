# ADR-029: 观测深度与路径 Matcher——Strand 知识层第一性重构

## Status: Accepted（supersedes ADR-028 §3/§4，amends §6 digest 形状；§2 深度表声明机制与 browser 例子、§3 Matcher 步的 Layer 语义 amended by [ADR-030](./030-collector-depth-declaration.md)）

## Date: 2026-07-20

> 设计定于 2026-07-20 destruct-rebuild grilling session。实现待动工；
> 用户明示不保已采数据与既有实现（先破后立）。

## Context

ADR-028 落地五个 commit 内连续两次被真实数据证伪（constellation 聚簇 → 特异性×时长
打分 → LLM 分诊），第三版（分诊）仍有三个结构性问题：

1. **组合型 Strand 无出生通道**。分诊看到的是摊平的 `(token, 分钟数, ≤3 邻居名)`——
   当一件事的所有把手都是世界知名工具（VALORANT + livehime + VRChat = 直播），
   逐 token 世界知识判定全部 Known，提问数恒为 0。ADR-028 张力 1 自己写过
   "意义是跨把手涌现的"，单把手化悄悄放弃了涌现簇。结构性倒挂：**叙事者拿全结构
   digest，发问者拿摊平三元组，后者却要做更难的判断**（"解释不了" ≠ "不认识 token"）。
2. **机器知识入了库**。TriageDecisions 把 known 判词与 gloss 存库再注入 prompt——
   把叙事 LLM 免费自带、每次现取且永远最新的世界知识，缓存成库里会腐的断言。
   这正是张力 2 想避免的"永久断言过期"，主语从 AE 换成了 gloss。
3. **Handle 固定粗粒度已被咬**。localhost 整个躺在哨兵名单里，主项目的本地开发
   时间对知识层隐形——"共享宿主被咬再拆"事实上已经发生。

第一性重推，从愿景（"x 年前的今天我在做什么"）与原始数据形状出发：一天的信息
分三层——**观测结构**（segments 已有，不需入库）、**世界知识**（叙事 LLM 自带，
入库即腐）、**私有语义**（只在用户脑中，唯一必须捞出入库的）。知识层的全部机制
围绕第三层重建。

## Decision

### 1. 知识库契约：只存用户亲口事实

库里恰好两类事实，均由用户确认或撰写：**私有释义**（单 Matcher 退化形态，
如"花生 = B 站实习时部门做的产品"）与**命名的线**（Strand：名字 + 自由释义 +
指纹）。机器世界知识**永不入库**——叙事时 LLM 现取，永远最新、零维护；
"别再问"由裁决记录本身表达（绑定过 / 静音过），不存判词内容。
TriageDecisions 整表拆除。

产出物形状 = **结构化脊柱 + 自由文本身体**：指纹（Matcher 集合）给机器做注入与
diff；名字 / 释义是自由文本，给人写、给 LLM 读，绝不加 schema。否决纯文档知识库
（LLM 读时自行匹配）："已裁决不再问"退化为软保证，且全部释义每天出境
（§7 隐私面变差）。

时间语义零字段：释义是钉在过去的事实，永不变假；**注入只在指纹当日命中时发生**，
不在场自动沉默——"过期"由在场性处理。锚点被真正改用（同一读数先后指两件事）
罕见，`validFrom/validTo` 维持留门不预建。`UpdatedAt` 只服务缓存判脏。
使用记录（何时用过 / 次数）**不存**——它是 Matcher × segments 的确定性 join，
将来要 UI 就读现算结果（派生原则）。

### 2. 观测深度：采集器契约声明的读数表，digest 长成深度树

**每个采集器在自身契约里声明一张有序观测深度表**（浅 → 深，层内可有多个读数）：

- system：L1 = 进程 / App；L2 = 窗口标题
- browser：L1 = URL、标签页标题；（未来）L2 = 页面内容摘要；L3 = DOM
- vscode（规划）：L1 = 仓库根；L2 = 文件路径

知识层不认识 app / url / domain 这些名字——它们是各采集器的私有词汇。同一读数上
的粗细（domain vs 全 URL）由谓词表达，不是深度。**观测深度同时是隐私敏感度轴**：
与 ADR-017"采集能力分层可拆、浅信任只装浅采集器"是同一张表——多用户化时，
浅信任用户的 Strand 指纹自动只能锚在浅层读数上。一处声明，三处受益
（知识层匹配、隐私分层、采集器路线图）。

**digest 的身份维度按深度表长成深度树**：节点 = (读数, 并集时长)，子节点 =
该读数在下一深度的分解，递归到该 Source 深度表尽头；渲染 = 确定性预算剪枝
（展开门槛、每节点子数封顶、尾部折叠成"其他 N 个"）。时间结构（轨 / 块 / 会话）
是正交维度，树挂在块上。`MaxTitlesPerBlock` 抽样退役——标题不是块的风味样本，
是块的下一深度**分布**（"Code 3.2h ── hyperframes-workspace 2.1h · heartbeat 0.8h ·
其他 5 个 0.3h"），"块内是一件事还是五件事"的信号由分解本身携带。

### 3. Matcher：深度树上的路径谓词

**Matcher（匹配子）** = 知识层指纹原子：沿某 Source 深度树的**路径谓词**——
各层 `(读数, 谓词, 值)` 的合取，谓词 ∈ {等于, 前缀, 包含}，单层是退化形态：

```
(system, L1 app = Code) ∧ (L2 title contains "hyperframes")
```

Strand 指纹 = Matcher 集合；**Mute 的单位也是 Matcher**。digest 是深度树的观测
投影，Matcher 是同一棵树上的路径谓词——发问 LLM 看着前者提案后者。粗粒度仍是
提案默认档；只在 digest 分解显示"粗读数下明显多件事"时才提案细一档。词汇表刻意
收窄（三种谓词起步），防止长成规则引擎。localhost 随哨兵名单拆除出狱，其身份由
路径 Matcher 恢复。新采集器带自己的深度表来插，**知识层零改动**（深模块：Matcher
语法是 interface，深度表是各采集器的 adapter 声明）。

### 4. 发问 = 与叙事同吃一个 digest 的判官

发问与叙事是**吃同一个 digest 的两次独立 LLM 调用**。digest 放两个 prompt 的共享
前缀（provider 前缀缓存命中）；发问 prompt 后段附 few-shot 裁决日志（bind / mute
各若干，空日志 = 冷启动裸判）与两行确定性注释（近 14 天高频读数；已裁决 / 已知
脉络标注）。输出 ≤3 条问题，各锚定：Matcher 提案（可含路径细化）+ 时段 +
一次性名字 / 释义提案，落到既有表单确认（§5 不变）。判断"什么是世界知识解释
不了的"**整体交给 LLM**——组合型问题（"12:00–15:00 VALORANT 与 livehime 并行
三小时，在直播？"）因看得见结构而免费出现。偏安静写进 prompt。

确定性两端不变更纯：digest 投影可测；封顶 ≤3 由确定性层对 LLM 输出裁剪；写库
路径原样。缓存与 recap 同构（按天 + 水位，失败不写缓存）；**用户裁决后对缓存
问题做确定性 diff 过滤（剔除锚定 Matcher 已被裁决的条目），零 LLM 重调**。

随 QuestionProjection 一起拆除的确定性选题装置：哨兵名单、噪声地板选题、
ubiquity 门槛、粗筛限流、共现邻居挑选——它们全是"看不见结构时的代理判据"。

### 5. Anchor / Satellite 降级为策展纪律

旧管线需要预先算强度角色，因为确定性层看不见结构。新设计里判官和叙事者都直接
看结构，A/S 从机制降级为两处纪律：

- **提案纪律**：指纹只收特异性 Matcher（锚点）；通用工具（blender / AE / 浏览器）
  **不进指纹**、写进自由释义（"做这个项目时通常开着 AE"）。归因在叙事时由 LLM
  对着时间线 + 释义完成——AE 存成永久成员的语义漂移问题从根上不再存在。
- **词汇表**：锚点 / 卫星作为讨论与 prompt 语言保留；无实体、无强度推断代码、
  无角色存储字段。

共现 Pattern **不做 Matcher 类型**：硬编码"同时"需要时间窗 DSL（预建规则引擎），
而叙事端拿自由释义免费完成同样的归因。真出现对 pattern 的硬统计 / UI 需求再上
（留门）。

### 6. 继承 ADR-028 存活部分

- §1 定位不变：手段先行，第二大脑留门。
- §2 Strand 核心对象、策展层非派生物、绝不写回 segment：不变（指纹单位换 Matcher）。
- §5 表单确认、确定性两端夹薄 LLM、无会话态：不变；对话式纠错继续留门。
- §6 缓存契约：段水位自动重生成 + Strand 变更只判脏只提示：不变。
- §7 隐私 trade-off：纯继承；注入只在指纹当日命中时发生，释义只在相关日出境。
- §8 裁决两出口（绑定 / Mute）：不变，单位换 Matcher。
- 不基于 recap 散文发问（有损派生物上不盖楼）：不变，并由此否决"Recap 二阶段"
  与对话式发问（张力 4 的否决理由原样成立）。

### 7. 拆除 / 保留清单（实现层）

**拆除**：QuestionProjection（摊平、哨兵、ubiquity、粗筛）；ITriageGenerator 的
per-handle 调用形状与 TriageDecisions 表及迁移；Handle 概念（固定粗粒度
(Source, token)）；StrandHandle / MutedHandle 的 token 行形状（换 Matcher 形状）；
RecapProjection 的 MaxTitlesPerBlock 抽样。

**保留**：Strand 实体骨架（名字 + 释义 + 成员集）；KnowledgeService 写路径与
幂等语义；RecapService 缓存契约；表单确认 UI 概念（提案卡改 Matcher 提案）；
`Dashboard → Analytics` 知识裁决写路径（CONTEXT-MAP 不变）。

### 被否决的备选

- **跨日共现 PMI 作第二问题源**：单把手前提下的补丁；前提推翻（判官看结构）后
  补丁不需要。
- **一次调用同出叙事 + 问题**：散文与严格 JSON 互相拖累、一处解析失败两个产品
  一起死、两边 prompt 需求已分歧（few-shot 日志）；"一致性"是伪需求——问题锚定
  digest 事实而非散文句子。
- **Recap 二阶段 / 对话式发问**：在散文上盖楼 + 引入会话态；ADR-028 张力 4 否决
  理由原样成立。
- **纯文档知识库（无结构指纹，LLM 读时匹配）**：diff 退化软保证、注入变成全库
  每日出境。
- **Pattern Matcher 类型**：时间窗 DSL 预建；释义 + 叙事 LLM 免费等价。
- **存机器 gloss / 判词（TriageDecisions）**：缓存世界知识必腐。
- **使用记录落库**：Matcher × segments 可现算，落库即复制 segments。

## Consequences

- ✅ 组合型 Strand 有了出生通道：判官看得见并行结构，"全知名把手、私有含义"
  的事问得出来。
- ✅ 库里每条都是用户说的，永不腐；机器知识每次现取最新。
- ✅ 知识层对新采集器零改动：深度表来插即用；观测深度 = 隐私轴，多用户分层
  采集与指纹能力自动对齐。
- ✅ LLM 调用从 ≤9 次/天（8 分诊 + 1 叙事）降到 2 次/天，且共享 digest 前缀缓存。
- ✅ digest 深度树同时服务叙事与发问，无第二套投影。
- ⚠️ 发问质量押在单次大 prompt 调用上；封顶 3 + 偏安静 + 失败不写缓存兜底。
- ⚠️ Matcher 路径谓词让提案卡 / 表单编辑复杂度略升（需展示与编辑路径）。
- ⚠️ 深度树预算（展开门槛、子数封顶）是新的调参面。
- ⚠️ ADR-028 §3（Handle 粗粒度、A/S 机制）、§4（粗筛 + 分诊选题）自本 ADR 起
  作废；TriageDecisions 数据随迁移丢弃（知情接受，见页首）。

## References

- [ADR-028](./028-strand-knowledge-layer.md) —— 被推翻的 §3/§4 与被继承的
  §1/§2/§5/§6/§7/§8；三次证伪的完整记录
- [ADR-023](./023-recap-cloud-llm-projection.md) —— digest 投影 / 缓存契约 /
  出境 trade-off（发问判官复用其全部纪律）
- [ADR-017](./017-activity-segment-pluggable-collectors.md) —— 分层采集：观测深度
  与"采集能力分层可拆"是同一张表
- [ADR-012](./012-input-event-tracking.md) —— 出境 trade-off 先例
- `server/CONTEXT.md` —— Observation Depth / Matcher / Asking 词条（本 ADR 同步更新）
- `CONTEXT-MAP.md` —— 知识裁决写路径（措辞随本 ADR 更新为 Matcher）
- 实现：待动工（`.scratch/` 重拆 issue）
