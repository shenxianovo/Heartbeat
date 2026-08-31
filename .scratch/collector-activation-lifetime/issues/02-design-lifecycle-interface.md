# 02 — Design It Twice 并冻结生命周期 Interface

Status: done

Owner: Collection / Collector Runtime + Protocol

Priority: P1 — seam 未固定前实现会继续把旧 coordinator 包在新名字里。

## Acceptance

- [x] 三个高推理设计分别给出最小 Interface、最强状态模型、最易迁移方案。
- [x] 每案包含 Interface、使用例、隐藏实现、依赖分类/adapter 与 trade-off。
- [x] 按 Depth、Locality、seam placement 比较，推荐案与拒绝理由写回 PRD/ADR。
- [x] 明确 Hub 与 Client 两个 owner，不跨进程合并。
- [x] TDD seam 经主任务确认后才开始写生产测试。

## Comments

### 2026-08-31 — Interface frozen

三案共同确认 Hub seam 必须覆盖 accepted Hello → Ready/terminal release，Client seam 必须覆盖 outbox
ordinary admission → background/drain/fenced。最终 hybrid 采用两方法 Hub Interface + persistent Terminal，
以及 lease-based Client Interface + drain-scoped tail admission。Interface、Stop policy、comparison 与删除
边界已写入 PRD 和 ADR-046；后续测试只落在 PRD 的四个 agreed seams。
