# Agent 规则

## 重写期约束

项目正在重写。重写完成前不部署，也不兼容旧数据、旧客户端或旧接口。

- 修改数据库结构时，直接改 Initial migration 和模型快照，不加增量迁移。
- 修改协议字段时，同时修改生产者和消费者，不升级协议版本。
- 只保留当前实现。代码、测试和文档一起更新，不加兼容层、迁移路径或版本协商。
- 用户明确要求前不建设 CI，在本地运行检查。
- API、Storage、Observers、Testing 和代码中有一部分仍按旧模型编写。它们与 Core 不一致时以 Core 为准，字段名也一样。待同步的内容记录在 `.scratch/` 下的规格中。

## 文档

长期文档只写在 `src/Docs/Heartbeat.Docs/content/docs/`。开始工作前，先读 [Introduction](src/Docs/Heartbeat.Docs/content/docs/start/index.mdx) 和 [Walkthrough](src/Docs/Heartbeat.Docs/content/docs/start/walkthrough.mdx)。开始领域工作前，再读 [Core Overview](src/Docs/Heartbeat.Docs/content/docs/core/index.mdx)、[Glossary](src/Docs/Heartbeat.Docs/content/docs/core/glossary.mdx)，以及相关的类型页、规则页和 ADR。

| 内容 | 位置 |
| --- | --- |
| 入口、文档地图和阅读路径 | `start/` |
| 类型的结构与规则 | `core/model/` |
| 跨类型的规则 | `core/rules/` |
| 术语 | `core/glossary.mdx` |
| 架构决定 | `adr/` |
| HTTP 契约 | `api/openapi.json` |
| 单个 Observer 的业务规则和实现决定 | `observers/<observer>/` |
| 模块的运行和操作说明 | 模块目录的 `README.md` |

使用 `grill-with-docs` 或 `domain-modeling` 时，`GLOSSARY.md` 对应 `core/glossary.mdx`，`docs/adr/` 对应 `adr/`。

Core 的规则带编号，前缀是所在页面的英文名称，例如 `OBSERVATION-9`。代码、测试和 ADR 直接引用编号。重写期间可以重新编号，但要同时更新所有引用；重写完成后编号固定，不再复用。写法见 [Core · Conventions](src/Docs/Heartbeat.Docs/content/docs/core/conventions.mdx)。ADR 用 slug 命名，没有编号。

Core 只记录用户已确认的结构与语义。Agent 可以按已确认的内容起草和整理文档。改变模型语义前，逐项取得用户确认。

## 设计决定

实施中的每项决定先在对话中说明，取得用户确认后再做。已确认范围内的工作可以继续。

以下三类决定写成 ADR：

- 责任归哪个组件。
- 协议的语义。
- 某类数据的权威位置。

较小的决定写入对应的类型页、规则页或契约。只涉及单个 Observer 的决定写入该 Observer 的文档。

## 传播层级

更新按以下层级前向传播，禁止反向修改：

T0 模型与决策（ADR、Core、Glossary）→ T1 对外约定（OpenAPI、API、Storage、Observers）→ T2 验证定义（Testing Case）→ T3 实现（代码和测试）。

从受影响的最高层开始修改，并传播到最远的受影响层。下层问题在本层解决不了时，先改最早能解决问题的上层，再重新前向传播。完整规则、各层内部顺序和图见 [ADR · 文档的权威与传播](src/Docs/Heartbeat.Docs/content/docs/adr/documentation-authority.mdx#layers)。

## 文档与实现分工

文档和实现由不同的任务负责。

- 实现任务只改代码和测试。文档站、各级 `README.md`、`AGENTS.md` 和 skill 都由文档任务修改。
- 实现任务发现文档需要修改时，仍按传播层级找到最早能解决问题的一层，但把改动写成需求，放进 `.scratch/<feature-slug>/doc-requests.md`，并告诉用户。每条需求写明位置（页面和规则编号）、问题，以及希望改成什么意思；只写意思，不写成稿。
- 文档任务按 [Core · Conventions](src/Docs/Heartbeat.Docs/content/docs/core/conventions.mdx#writing) 的文字和用词把需求写进文档，用自己的语言重新组织，需求原文只作参考。

## 工作约定

- 进行中的规格和 issue 放在 `.scratch/<feature-slug>/`，完成后删除。
- 修改应用实现时，用 [verify-heartbeat](.agents/skills/verify-heartbeat/SKILL.md) 选择验证。
- 修改文档站、`README.md`、`AGENTS.md` 或 skill 后，在 `src/Docs/Heartbeat.Docs` 运行 `pnpm docs:check`，修复全部报错再结束任务。
- 修改文档站时，另外运行 `pnpm test`、`pnpm types:check` 和 `pnpm build`。修改页面或交互时，还要打开实际页面检查。
- 实现中用过的概念从模型中删除时，在 [Glossary · 不再使用的术语](src/Docs/Heartbeat.Docs/content/docs/core/glossary.mdx#deprecated) 加一行。
- 结束任务前，检查改动范围内的代码、文档和链接是否一致，报告验证结果和未验证的范围。
