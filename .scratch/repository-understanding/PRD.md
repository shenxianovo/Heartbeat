# 仓库理解与 Agent 可协作性收口

Status: needs-triage

## 目标

让人和 Agent 都能从仓库级入口逐层走到项目与深模块，能够判定本地 E2E 是否真的产生正确
数据，并让历史兼容债务和人工验收门禁有稳定账本。

## 当前结论

- 一键本地栈已经解决“如何启动”，但此前只验证 HTTP 与容器存活，没有验证恢复数据和新客户端
  写入的数据语义。
- 根 README、系统架构、Context/ADR、项目 README 的层级基本存在，但项目清单已经发生漂移，
  owner 主导的逐层走查现已完成；项目清单由 solution/package manifest 负责，README 只维护
  稳定目录责任。
- 兼容债务散在注释与历史 issue 中，现已建立初始 ledger；具体支持窗口和删除优先级需要 owner
  裁决。
- Agent issue closeout 与 friction closeout 已补充，但仓库其他 feature 的陈旧状态尚未批量清理。
- Collector Package Web 交付方向已经完成 owner 裁决并转入独立
  [Collector Package Registry PRD](../collector-package-registry/PRD.md)；这里的设计 issue 已 closeout，
  不代表 Registry 或 P1 可靠性修复已经实现。
- `d69735b` 删除了仍为 `ready-for-agent` / `ready-for-human` 的 lifecycle 账本；扫描已恢复
  cross-platform、local-calendar 以及其他被删的非 terminal issue 原文。恢复不自动证明旧状态仍准确，
  对应 PRD/issue 必须以当前代码证据重新 closeout。

## Issues

1. [01 — 建立恢复数据与新客户端数据 smoke](issues/01-local-data-smoke.md)
2. [02 — Owner 逐层走查仓库文档](issues/02-owner-documentation-walkthrough.md)
3. [03 — 裁决兼容债务支持窗口与移除顺序](issues/03-compatibility-debt-retirement.md)
4. [04 — 建立 Agent friction 与 issue closeout](issues/04-agent-friction-closeout.md)
5. [05 — 让 Segment strict ingest 返回可判定结果](issues/05-strict-segment-ingest-outcomes.md)
6. [06 — 设计 Collector Package 托管与下载](issues/06-package-registry-delivery-design.md)
