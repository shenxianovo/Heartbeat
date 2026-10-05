# Agent 规则

## 重写约束

重写完成前不部署，也不兼容旧数据、旧客户端或旧接口。

- 数据库结构直接修改 Initial migration 和 model snapshot，不加增量迁移。
- 协议字段及其生产者、消费者直接一起修改，不为重写期行为升级版本。
- 只保留当前实现；同步更新代码、测试和文档，不加兼容层、迁移路径或版本协商。
- 用户明确要求前不建设 CI；在本地运行适当检查。

## 文档权威位置

长期文档的唯一权威位置是 `src/Docs/Heartbeat.Docs/content/docs/`。领域工作开始前，读[模型概览](src/Docs/Heartbeat.Docs/content/docs/index.mdx)、[术语表](src/Docs/Heartbeat.Docs/content/docs/glossary.mdx)和相关模型节点及 ADR。

使用 `grill-with-docs` 或 `domain-modeling` 时，将技能的文档路径映射如下，直接读写站内源文件：

- `GLOSSARY.md` 对应 `src/Docs/Heartbeat.Docs/content/docs/glossary.mdx`。
- `docs/adr/` 对应 `src/Docs/Heartbeat.Docs/content/docs/adr/`。
- 模型正文位于 `src/Docs/Heartbeat.Docs/content/docs/model/`。

模型正文只记录用户已确认的结构与语义。Agent 按已确认内容起草和整理文档；改变模型语义前，逐项得到用户确认。

## 设计决策

实施中的每项决定先在对话中说明并得到用户确认；已确认范围内的工作可以继续。

以下决策必须在文档站的 `adr/` 留 ADR：

- 责任归哪个组件；
- 协议的语义；
- 某类数据的权威位置。

较小决定写入对应模型或契约文档；模块自身的操作说明写入模块 README。

## 工作约定

- 进行中的规格和 issue 放在 `.scratch/<feature-slug>/`，完成后删除。
- 应用实现变更使用 [verify-heartbeat](.agents/skills/verify-heartbeat/SKILL.md) 选择验证。
- 文档站变更在 `src/Docs/Heartbeat.Docs` 运行 `pnpm types:check` 和 `pnpm build`；页面或交互变更还需检查实际页面。
- 结束任务前检查改动范围内的代码、文档与链接是否一致，并报告验证结果和未验证范围。
