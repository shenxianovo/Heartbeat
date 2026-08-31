# 02 — Design It Twice 并冻结生命周期 Interface

Status: ready-for-agent

Owner: Collection / Collector Runtime + Protocol

Priority: P1 — seam 未固定前实现会继续把旧 coordinator 包在新名字里。

## Acceptance

- [ ] 三个高推理设计分别给出最小 Interface、最强状态模型、最易迁移方案。
- [ ] 每案包含 Interface、使用例、隐藏实现、依赖分类/adapter 与 trade-off。
- [ ] 按 Depth、Locality、seam placement 比较，推荐案与拒绝理由写回 PRD/ADR。
- [ ] 明确 Hub 与 Client 两个 owner，不跨进程合并。
- [ ] TDD seam 经主任务确认后才开始写生产测试。

