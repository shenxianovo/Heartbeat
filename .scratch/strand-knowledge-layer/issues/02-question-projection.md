# 02: 提问器投影 —— diff + 时间贴邻聚簇 + Anchor/Satellite

Status: ready-for-agent

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

- [ ] 时间贴邻聚簇:同日**不交错**的两活动(打游戏 vs 做项目来回切前台)不入同簇(单测)
- [ ] 闸:低于噪声地板 / 不复现的簇不产问题
- [ ] diff:已 Mute 或已绑定 Strand 的把手不再产问题
- [ ] 自锚优先:已成某 Strand 锚点的把手,不被当天别的锚点吸走(单测)
- [ ] 每天封顶 1–3,按未解释时长排序
- [ ] 全流程纯函数 / 可脱离 DB 与 HttpContext 构造测试(投影可单测纪律,ADR-023 §2)

## Blocked by

- [01](./01-foundation-model-and-commit.md)
