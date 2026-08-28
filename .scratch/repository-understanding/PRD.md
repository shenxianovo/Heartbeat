# 仓库理解与 Agent 可协作性收口

Status: ready-for-human

## 目标

让人和 Agent 都能从仓库级入口逐层走到项目与深模块，能够判定本地 E2E 是否真的产生正确
数据，并让历史兼容债务和人工验收门禁有稳定账本。

## 当前结论

- 一键本地栈已经解决“如何启动”，但此前只验证 HTTP 与容器存活，没有验证恢复数据和新客户端
  写入的数据语义。
- 根 README、系统架构、Context/ADR、项目 README 的层级基本存在，但项目清单已经发生漂移，
  并缺少一次 owner 主导的逐层走查。
- 兼容债务散在注释与历史 issue 中，现已建立初始 ledger；具体支持窗口和删除优先级需要 owner
  裁决。
- Agent issue closeout 与 friction closeout 已补充，但仓库其他 feature 的陈旧状态尚未批量清理。

## Issues

1. [01 — 建立恢复数据与新客户端数据 smoke](issues/01-local-data-smoke.md)
2. [02 — Owner 逐层走查仓库文档](issues/02-owner-documentation-walkthrough.md)
3. [03 — 裁决兼容债务支持窗口与移除顺序](issues/03-compatibility-debt-retirement.md)
4. [04 — 建立 Agent friction 与 issue closeout](issues/04-agent-friction-closeout.md)
