---
name: verify-heartbeat
description: Verify Heartbeat implementation changes with the repository Developer CLI. Use after building, fixing, refactoring, or reviewing Heartbeat code, and whenever evidence is needed for UI, API, Hub, or macOS Collector behavior.
---

# Verify Heartbeat

使用仓库 Developer CLI 执行验证，并只陈述证据实际证明的范围。

1. 修改前确定 Git 比较基点：普通工作树用 `HEAD`，分支工作用用户指定分支或 merge base。
2. 从 [Feature Map](references/features/README.md) 找到相关能力，并读取它链接的权威文档。
3. 先运行 `dotnet run --project tools/Heartbeat.Dev -- verify closeout --base <ref> --plan`，核对选择结果后去掉 `--plan` 执行。它同时运行变更验证和结构质量闸门；分析器不可用视为失败。
4. UI、用户流程、HTTP 展示或原生运行时变更，运行对应 `scenario`。活动泳道性能变更运行前端 README 中的生产基准；若交互行为也变了，同时运行回放场景。基于 fixture 的浏览器场景不能称为真实端到端测试。
5. 检查同次运行的 `manifest.json`、`closeout.json`、`quality.json` 和相关报告，确认变更验证与质量闸门都通过；性能基准报告由前端脚本独立保存。最终说明检查项、结果、证据路径与限制。
6. 路由、用户入口、稳定选择器、renderer 或场景命令变化时，同步更新 Feature Map。

命令契约、阈值、敏感证据规则和环境准备以 [工程验证](../../../docs/verification.md) 为准。
