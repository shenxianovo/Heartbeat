# 文档导航

日常开发从 [Development Guide](development.md) 开始。讨论稿和历史验收不代表当前实现。

| 用途 | 入口 |
| --- | --- |
| 领域边界与术语 | [Context Map](../CONTEXT-MAP.md) → 各上下文 glossary |
| 当前架构与协议 | [系统总览](architecture/system-overview.md)、[Contracts](../collection/contracts/README.md)、[Conformance](../collection/protocol/conformance/README.md) |
| 开发 Desktop 与持久 Profile | [开发指南](development.md)、[ADR-053](adr/053-desktop-profile-and-installation-ownership.md) |
| 临时验收泳道 | [验证器命令](../tools/Heartbeat.Verification/README.md)、[覆盖与设计边界](architecture/automated-verification-design.md) |
| Browser 构建与开发隔离 | [Browser README](../collection/collectors/Heartbeat.Collector.Browser/README.md)、[开发绑定与更新](architecture/browser-development-binding.md) |
| 独立交付 | [交付现状与剩余验收](architecture/collector-delivery-implementation-roadmap.md) |
| API / 数据库 | [API 导读](api.md)、[数据库导读](db.md)；字段真相源是 OpenAPI 和实体/迁移 |
| 运维与升级 | [Runbooks](runbooks/README.md)、[兼容债务及退出条件](architecture/compatibility-debt.md) |
| 决策与验收证据 | [ADRs](adr/)、[历史主链路验收](verification/2026-09-07-08-main-path-evidence.md)、[本地 tracker](../.scratch/) |

## 待定设计

- [Monorepo 发布设计](architecture/monorepo-release-design.md)：独立版本与手动发布入口仍在澄清。
- [观测模型](architecture/collector-observation-model-proposal.md)：当前沿 Collector 运行模型验证推进。
- [Collector SDK](architecture/collector-sdk-design.md)：协议与 SDK 设计暂停。
- [观测存储候选](architecture/observation-storage-design.md)：统一对象登记方案已暂停，不能作为迁移基线。
- [FOI 研究笔记](architecture/feature-of-interest-research.md)：参考材料，不是架构决定。

## 2026-09-10 整理结论

删除交付路线图中已结束的 tracer 操作步骤、重复流程图和逐日修正，保留当前边界及原始 issue/ADR 入口。
Browser README 的单次发布流水账归回 issue 07，不再维护第二份验收状态。

以下内容暂不整篇删除：

- ADR 即使被取代也解释取舍；保留退役标记和后继链接。
- 验收记录保存源码/制品身份及未关闭失败；一次通过不能替代当前回归。
- 数据 smoke、数据刷新、验证器分别验证历史不变量、恢复快照和指定行为，泳道不能替代前两者。
- 日历窗口及原生 Fact 升级 runbook 仍保存切换/回滚边界，不能仅因开发隔离已存在而删除。
- 暂停的 SDK/存储讨论仍被当前观测模型引用。如明确放弃，可将独有结论并入决策后删除长稿；
  本轮不把“暂停”改成“废弃”。
