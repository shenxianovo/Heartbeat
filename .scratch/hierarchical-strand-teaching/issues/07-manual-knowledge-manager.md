# 07: 手动知识管理器

Status: ready-for-agent

## Parent

[PRD](../PRD.md) · [ADR-031](../../../docs/adr/031-hierarchical-strand-episode-teaching-loop.md)

## What to build

提供不依赖当天问题的手动管理入口，让用户查看和修正 Strand 树、Matcher/Mute、Episode 及 RecurrenceProbe。手动入口与教学流共享领域服务和身份规则，不能形成绕过约束的第二套写模型。

### Strand tree

- 以树展示 Strand，节点显示名称、有效日期、Gloss、Matcher 数量及完整 path；支持查看已结束时期，并能区分同父同名的不同日期范围。
- 支持创建、编辑、结束和纠错式移动。所有现有节点按 UUIDv7 操作，服务端约束和并发检查与教学 commit 一致。
- 移动前显著说明它会重写历史解释；现实归属变化提供“结束旧节点并在新父级下建立后继”的引导，而不是用 Move 模拟。
- 结束有活跃子节点的父级时显示服务端返回的子节点，并要求用户明确选择：先结束/调整子级，或建立适当后继；不静默级联。
- 父级可作为零 Matcher 的纯语境容器，UI 不强迫为每个节点创建触发器。

### Matcher and mute management

- 在 Strand 下新增、查看和删除 Matcher，展示 canonical path/value，避免看似不同但业务等价的重复谓词。
- 提供可理解的 Mute 查看与解除能力，并说明 Mute 只抑制相似知识问题，不删除 Observation 或 Recap 历史。
- Matcher/Mute 的持久化 ID 使用 UUIDv7，但编辑去重继续基于 canonical predicate。

### Episode and Probe management

- 支持按日期、关联 Strand 和未关联状态浏览 Episode；编辑文本/近似时间，关联或解除一个最具体 Strand。
- 可从 Episode 发起非破坏性提升：选择或新建 Strand、可选建立 Matcher、关联 Episode，原 Episode 始终保留。
- 展示活跃与已解决 Probe；允许提升、否认、静音，解决后不再作为待处理项。
- 不提供“把 Episode 直接转成 Strand”或自动批量关联历史 Episode 的操作。

### Freshness and feedback

- 修改知识后不在管理页批量重生成 Recap。受影响历史日期由 issue 03 在读取时惰性提示。
- 所有表单正确处理验证错误和并发冲突，保留用户输入并支持刷新最新状态。

### Tests

- 覆盖树 CRUD/移动/结束冲突、同名时期展示、Matcher canonical 去重、Episode 编辑关联、Probe 解决和提升。
- 验证管理 UI 调用共享服务、跨 Owner ID 被拒绝、知识修改不会自动触发批量 LLM 生成。

## Acceptance criteria

- [ ] 用户可浏览完整 Strand 树、历史时期、path、日期、Gloss 和 Matcher 状态
- [ ] 创建/编辑/结束/移动均按 UUIDv7 并复用服务端树、日期和并发约束
- [ ] Move 与现实归属变化在 UI 中有明确不同的流程和警告
- [ ] 结束含活跃子节点的父级需要显式处理，不发生静默级联
- [ ] 父 Strand 可零 Matcher，Matcher/Mute 可按 canonical predicate 管理
- [ ] Episode 可按日期/Strand 浏览、编辑并关联至最多一个 Strand
- [ ] Probe 可提升、否认或静音；提升保留 Episode 且不自动关联其他历史记录
- [ ] 知识修改不会从管理页触发历史 Recap 批量重生成
- [ ] 前后端测试覆盖关键管理操作、Owner 隔离、验证和并发冲突

## Blocked by

- [01](./01-hierarchical-temporal-strand.md)
- [02](./02-episode-recurrence-probe.md)

## Comments
