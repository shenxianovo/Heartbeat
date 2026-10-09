---
name: verify-heartbeat
description: Verify Heartbeat changes with the repository Developer CLI and select additional evidence for API, Docs, Compose, and developer-tool behavior.
---

# Verify Heartbeat

使用仓库 Developer CLI 执行验证，并只陈述证据实际证明的范围。

1. 修改前确定 Git 比较基点：普通工作树用 `HEAD`，分支工作用用户指定分支或 merge base。
2. 从 [Feature Map](references/features/README.md) 找到相关能力，读取它链接的权威文档，并准备 [Developer CLI README](../../../tools/Heartbeat.Dev/README.md) 中的本机依赖。
3. 运行 `dotnet run --project tools/Heartbeat.Dev -- verify closeout --base <ref> --plan` 核对选择结果，然后去掉 `--plan` 执行。它组合变更验证和质量观察：构建、测试或扫描未完成返回失败，质量发现本身供审查。
4. Compose 变更使用独立项目、端口和数据卷验证所选栈的启动、就绪、停止与数据保留。页面、HTTP 展示和交互变更检查实际页面及相关行为。自动测试使用独立数据库；只有直接观察到的运行范围才能称为已验证。
5. 检查同次运行的 `manifest.json`、`verification.json`、`closeout.json`、`quality.json` 和相关报告，报告结果、证据位置与未验证范围。基线无法编译时，报告比较未完成；同时核对已保存的当前扫描，使用可编译的隔离用例验证扫描流程，不能将它称为原基线比较通过。
6. 用户入口、路由或验证命令变化时，同步更新 Feature Map。

操作与保留规则见 [Developer CLI README](../../../tools/Heartbeat.Dev/README.md)，职责见 [ADR 0007](../../../src/Docs/Heartbeat.Docs/content/docs/adr/0007-developer-cli-and-verification.mdx)。
