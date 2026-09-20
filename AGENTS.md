# Agent 规则

## 重写约束

重写完成前不部署，也不兼容旧数据、旧客户端或旧接口。

- 数据库结构直接修改 Initial migration 和 model snapshot，不加增量迁移。
- 协议字段及其生产者、消费者直接一起修改，不为重写期行为升级版本。
- 只保留当前实现；同步更新代码、测试和文档，不加兼容层、迁移路径或版本协商。
- 用户明确要求前不建设 CI；在本地运行适当检查。

## 设计决策

任务暴露设计或架构问题时，先说明影响并请用户确认，再实施该决策；已确认范围内的独立修改可以继续。

以下决策必须在 `docs/adr/` 留 ADR：

- 责任归哪个组件；
- 协议的语义；
- 某类数据的权威位置。

较小决定写入对应契约文档或模块 README。

## 工作约定

- 开始领域工作前读 `CONTEXT.md` 和相关 ADR，细则见 [领域文档](docs/agents/domain.md)。
- 进行中的规格和 issue 放在 `.scratch/<feature-slug>/`，完成后删除；细则见 [本地 issue](docs/agents/issue-tracker.md)。
- 使用固定的五类 triage 标签，见 [标签映射](docs/agents/triage-labels.md)。
- 实现变更使用 [verify-heartbeat](.agents/skills/verify-heartbeat/SKILL.md) 选择并执行验证。
- 结束任务前执行 [收口检查](docs/agents/closeout.md)。
