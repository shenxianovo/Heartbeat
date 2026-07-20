# 02: 提问器投影 —— diff + 时间贴邻聚簇 + Anchor/Satellite

Status: done

## Parent

[ADR-028](../../../docs/adr/028-strand-knowledge-layer.md)

## What to build

提问器 = ADR-023 §2 投影的**第三出口**（ADR-028 §4）。纯函数/可脱离 DB 构造的服务:输入某 Owner 某日的 segments + 该 Owner 的 Strands+Mutes,输出候选提问簇(0..N)。**本片不含 LLM**——提案的 name/gloss 留空,只产"该问哪个簇、簇里有哪些把手"。

- **聚簇看时间贴邻,不看裸同日(ADR-028 §4/§3)**:成员须与锚点段**分钟级交错**(同一注意力语境)。仅"同一天出现过"不入簇——防同日无关活动(如交错打的游戏)被吸进项目簇。
- **闸(ADR-028 §4)**:复现 + 有意义时长(≥ recap 噪声地板 60s,复用 ADR-023 <60s 丢弃纪律)。低于地板/不复现的不产问题。
- **diff**:剔除已被 Strand 覆盖或已 Mute 的把手/簇。
- **自锚优先(ADR-028 §3)**:已裁决的把手(某 Strand 的锚点,或已 Mute)**退出跟车逻辑**,摊布再像卫星也不被当天别的锚点吸走。
- **Anchor/Satellite 强度(ADR-028 §3)**:从摊布特异性推断,冷启动用 Source/类型作弱先验;**v1 可粗**(先验为主,摊布精算可后续深化)。簇以 anchor 作命名候选主体。
- **封顶**:每天最多端 1–3 个,按"未解释时长"降序取头部。

## Acceptance criteria

- [x] 时间贴邻聚簇:同日**不交错**的两活动(打游戏 vs 做项目来回切前台)不入同簇(单测)
- [x] 闸:低于噪声地板 / 不复现的簇不产问题
- [x] diff:已 Mute 或已绑定 Strand 的把手不再产问题
- [x] 自锚优先:已成某 Strand 锚点的把手,不被当天别的锚点吸走(单测)
- [x] 每天封顶 1–3,按未解释时长排序
- [x] 全流程纯函数 / 可脱离 DB 与 HttpContext 构造测试(投影可单测纪律,ADR-023 §2)

## Comments

- 2026-07-19 实现落地:`QuestionProjection`(纯静态,`HandleInterval`/`HandleRef`/`QuestionCluster` 类型)+ `QuestionService`(薄 DB 装配:查当日段→HandleDerivation→区间;载 strand 成员∪mute;14d 回看派生复现)。测试 14 个(10 纯投影 AC + 4 真库集成),服务端套件 99/99 绿。无 controller(端点属 issue 03),已在 Program.cs 注册 service。
- **落地时定的启发式旋钮(常量,易调,待用户确认)**:
  - 会话间隔阈值 `SessionGapSeconds = 600`(10 min):间隔超此即断为不同注意力会话。
  - 噪声地板 `NoiseFloorSeconds = 60`:复用 ADR-023,per-把手当日累计低于此聚簇前剔除。
  - gate 语义取 **"有意义时长(≥20min) OR 复现"** 而非严格"必须复现"——否则首日 4h 的新项目会被漏问。AC 里"非复现不问"因此精确化为"既不够时长又不复现才不问"。
  - 强度 v1 粗:锚点 = 簇内 recurring 优先、再按当日时长取头部;ADR-028 §3 的摊布特异性精算留后续深化。
  - 同一 constellation(把手集相同)跨会话合并为一簇、时长累加。

## Blocked by

- [01](./01-foundation-model-and-commit.md)

- 2026-07-20 ADR-029 推翻本片机制（摊平投影/分诊选题）：拆除与重建见 `.scratch/observation-depth-matcher/`。本文件保留为历史记录。
