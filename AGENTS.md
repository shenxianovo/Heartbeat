# Agent 规则

## 重写期约束

项目正在重写，重写何时结束由用户宣布。后端和客户端在重写完成后才部署。文档站已经发布，见 [ADR · 文档站的发布](src/Docs/Orisumi.Docs/content/docs/adr/documentation-publishing.mdx)。重写期间只保留当前实现，旧数据、旧客户端和旧接口直接替换，运行中的环境可以停止，数据可以清空。

- 被取代的代码、字段、测试和文档在同一次改动中删除。不加弃用标记，不写兼容层、回退分支或并行的旧实现。
- 修改数据库结构时，直接改 Initial migration 和模型快照。
- 修改协议字段时，在同一次改动中修改生产者和消费者。
- 代码、测试和文档一起更新。
- 检查在本地运行。CI 等用户提出后再建。
- API、Storage、Observers、Hosting、Testing 和代码中有一部分仍按旧模型编写。它们与 Core 不一致时以 Core 为准，字段名也一样。待同步的内容记录在 `.scratch/` 下的规格中。

## 文档

长期文档只写在 `src/Docs/Orisumi.Docs/content/docs/`。开始工作前，先读 [Introduction](src/Docs/Orisumi.Docs/content/docs/start/index.mdx) 和 [Walkthrough](src/Docs/Orisumi.Docs/content/docs/start/walkthrough.mdx)。开始领域工作前，再读 [Core Overview](src/Docs/Orisumi.Docs/content/docs/core/index.mdx)、[Glossary](src/Docs/Orisumi.Docs/content/docs/core/glossary.mdx)，以及相关的类型页、规则页和 ADR。

| 内容 | 位置 |
| --- | --- |
| 入口、文档地图和阅读路径 | `start/` |
| 类型的结构与规则 | `core/model/` |
| 跨类型的规则 | `core/rules/` |
| 术语 | `core/glossary.mdx` |
| 规则的格式与编号 | `core/conventions.mdx` |
| 文字、用词和导航 | `core/writing.mdx` |
| 架构决定 | `adr/` |
| HTTP 契约 | `api/openapi.json` |
| 单个 Observer 的业务规则和实现决定 | `observers/<observer>/` |
| 托管方的职责、Runtime 的进程与管道协议、安装包、服务器上的运行、Observer 的统一管理 | `hosting/` |
| 模块的运行和操作说明 | 模块目录的 `README.md` |

使用 `grill-with-docs` 或 `domain-modeling` 时，`GLOSSARY.md` 对应 `core/glossary.mdx`，`docs/adr/` 对应 `adr/`。

代码、测试和 ADR 用编号引用 Core 规则，例如 `OBSERVATION-9`，写法见 [Core · Conventions](src/Docs/Orisumi.Docs/content/docs/core/conventions.mdx)。重写期间可以重新编号，同时更新所有引用。重写完成后编号固定。ADR 用 slug 命名。

## 设计与变更

完整规则见 [ADR · 设计层与变更流程](src/Docs/Orisumi.Docs/content/docs/adr/documentation-authority.mdx#rules)。

- Core 只记录用户已确认的结构与语义。改变目标、模型语义或用户可以观察到的行为，做重要的取舍（例如持续的费用、新的部署依赖），或现有要求不足以选择方向时，先在对话中说明，取得用户确认后再做。其余工作在已确认的范围内直接完成。
- 设计分三层：目标与模型、组件与契约、实现，上层约束下层。开始前写明这次改动取代什么。变更从最早需要改变的位置开始，沿实际依赖同步到同层、下层、消费者和测试；每个位置改写、删除或写明不用改的理由。
- 责任归哪个组件、协议的语义、某类数据的权威位置，写成 ADR。较小的决定写入对应的类型页、规则页或契约，只涉及单个 Observer 的写入它的文档。

## 文档与实现分工

文档和实现由不同的任务负责。

- 实现任务只改代码和测试。文档站、各级 `README.md`、`AGENTS.md` 和 skill 都由文档任务修改。
- 修改接口时，文档任务先改契约，实现任务在一次改动中同步生产者、消费者和测试。
- 实现任务发现文档需要修改时，找到最早能解决问题的位置，把改动写成需求，放进 `.scratch/<feature-slug>/doc-requests.md`，并告诉用户。每条需求写明位置（页面和规则编号）、问题，以及希望表达的意思。
- 文档任务按 [Core · Writing](src/Docs/Orisumi.Docs/content/docs/core/writing.mdx) 的文字和用词把需求写进文档，用自己的语言重新组织，需求原文只作参考。

## 工作约定

- 运行或预览应用时，用 `orisumi env up` 启动本地环境，用 `orisumi env status --json` 读取入口地址，见 [Orisumi.Dev README · 环境](tools/Orisumi.Dev/README.md#环境)。
- 进行中的规格和 issue 放在 `.scratch/<feature-slug>/`，完成后删除。
- 实现中用过的概念从模型中删除时，在 [Glossary · 不再使用的术语](src/Docs/Orisumi.Docs/content/docs/core/glossary.mdx#deprecated) 加一行。

## 完成标准

结束任务前，按改动的类型完成对应的检查：

- 应用实现：用 [verify-orisumi](.agents/skills/verify-orisumi/SKILL.md) 选择并运行验证。
- 文档站、`README.md`、`AGENTS.md` 或 skill：在 `src/Docs/Orisumi.Docs` 运行 `pnpm docs:check`，修复全部报错。
- 文档站：另外运行 `pnpm test`、`pnpm types:check` 和 `pnpm build`。
- 页面或交互：在本地环境中打开实际页面检查。

验证的力度看出错的后果、影响范围、能否撤回，以及错误是否容易发现。所有改动都要检查改动范围内的代码、文档和链接是否一致。

收口时说明：最初的问题是否解决；同步了哪些位置，删除了哪些被取代的部分，哪些确认不用改；证据覆盖的范围；未完成和未验证的部分，以及承接它们的任务。见 [ADR · 设计层与变更流程](src/Docs/Orisumi.Docs/content/docs/adr/documentation-authority.mdx#closing)。
