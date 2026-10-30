# Orisumi.Docs

Orisumi.Docs 是 Orisumi 的文档站，使用 Fumadocs 和 Next.js。项目的长期文档都写在 [content/docs](content/docs)。公开站点是 <https://docs.orisumi.dev>，发布方式见[发布](#发布)。

文档站程序使用 [AGPL-3.0-only](../../../LICENSE)，文档内容使用 [CC BY-NC-SA 4.0](../../../LICENSE-DOCS)。范围见[仓库许可](../../../README.md#许可)。

## 运行

前置条件：Node.js 24 LTS 和 pnpm 12.3.4。

在本目录运行：

```sh
pnpm install --frozen-lockfile
pnpm dev
```

打开 <http://localhost:3000/>。通过 `orisumi env up` 启动本地环境时，文档站已经在同一地址。

| 命令 | 作用 |
| --- | --- |
| `pnpm docs:check` | 检查项目文档的一致性，见[文档检查](#文档检查) |
| `pnpm test` | 单元测试 |
| `pnpm types:check` | 生成路由类型并做类型检查 |
| `pnpm build` | 静态导出到 `out/`，并检查导出的页面、链接和资源 |

开发服务器不按静态导出运行，导出不支持的写法到 `pnpm build` 时才报错。

## 发布

公开站点由 `.github/workflows/docs.yml` 发布到 GitHub Pages，域名是 `docs.orisumi.dev`。这样做的理由见 [ADR · 文档站的发布](content/docs/adr/documentation-publishing.mdx)。

工作流在 main 上有相关改动时运行，也可以在 Actions 页面手动运行。相关改动包括本站目录、`src/` 下的 Core、Backend 和 Observers、`tests/`、各级 `README.md` 和 `AGENTS.md`、`.agents/`，以及工作流本身。构建依次：

1. 用完整的 Git 历史检出，供页面历史使用。
2. 运行 `pnpm docs:check`、`pnpm test` 和 `pnpm types:check`。
3. 运行集成测试，在 `TestResults` 中生成这次提交的报告。
4. 运行 `pnpm build`，上传 `out/`，再由 GitHub Pages 发布。

任何一步失败都不发布，线上保留上一次的站点。工作流只使用 GitHub 提供的令牌，不需要 PAT 或 Secrets。

仓库设置和 DNS：

- Pages 的发布来源是 GitHub Actions，自定义域名是 `docs.orisumi.dev`，开启 Enforce HTTPS。
- `docs.orisumi.dev` 有四条 A 记录，指向 GitHub Pages 的 `185.199.108.153` 到 `185.199.111.153`。域名所有权验证的 TXT 记录保留。
- GA4 的衡量 ID 是 `G-WKHX52M7JW`，只在公开站点采集。Search Console 通过 Cloudflare 中的 TXT 记录验证，这条记录也保留。

发布后在公开站点检查：

- 首页跳转到 `/start/`，HTTPS 证书有效，不存在的地址显示 404 页面。
- 中文搜索和分区筛选。
- 模型关系图和 `/start/graph`。
- API 参考页，以及页面上没有 Playground。
- 测试报告和一个 Case 页面，报告的提交 SHA 是这次发布的提交。
- 一个页面的 `.md` 地址、`/llms.txt` 和 `/llms-full.txt`。
- 在 Search Console 提交 `https://docs.orisumi.dev/sitemap.xml`。

## 文档检查

`pnpm docs:check` 检查项目文档：文档站内容（包括 `openapi.json`）、仓库中每个 `README.md` 和 `AGENTS.md`，以及 `.agents/` 下的 skill。`src/` 和 `tests/` 下的 C#、TypeScript 和 Swift 代码也会检查其中引用的规则编号和不再使用的术语，文档站自己的代码除外。任何一项不通过，命令返回非零。

| 检查 | 内容 |
| --- | --- |
| `link` | 相对链接和站内绝对链接指向的文件存在。`/api/…` 是由契约生成的页面，不检查 |
| `anchor` | 锚点对应显式的 `[#id]`，或者标题按 github-slugger 生成的 slug |
| `rule-heading` | Core 规则标题是 `### 前缀-n 摘要 [#前缀-n]`，前缀属于本页，页内编号唯一且递增。前缀与页面的对应关系取自 Core Conventions 页的前缀表 |
| `rule-actor` | Core 规则标题下的第一段是 `定义`，或者 `执行方：` 加上 `后端`、`写入方`、`读取方` 中的一个或多个，用 `、` 分隔 |
| `rule-reference` | 文档和代码中出现的规则编号都存在 |
| `rule-link` | `[前缀-n](…)` 链接到这条规则所在的页面和锚点 |
| `deprecated-term` | 文档和代码中不出现 [Glossary · 不再使用的术语](content/docs/core/glossary.mdx) 中的术语。带空格的术语只检查文档 |
| `frontmatter` | 文档站页面都有 `title` 和 `description` |
| `adr-structure` | ADR 依次包含背景、决定、理由、代价、规则位置；流程类 ADR 可以在决定之后加“规则” |
| `adr-index` | ADR 目录表中“决定”一栏与对应 ADR 的 `description` 相同 |
| `json` | ` ```json ` 代码块可以解析，带标题或行高亮的代码块也检查 |
| `sidebar` | `meta.json` 中的条目存在，每个页面都被某个 `meta.json` 收录 |

## 写作

写作约定见仓库根目录的 [AGENTS.md](../../../AGENTS.md)、[Core · Writing](content/docs/core/writing.mdx) 和 [Core · Conventions](content/docs/core/conventions.mdx)。

- 每个分区是 `content/docs/` 下的一个目录。侧边栏的顺序由各目录的 `meta.json` 决定，`---标题---` 添加分组标题，`...目录` 展开一个目录。
- [content/docs/api/openapi.json](content/docs/api/openapi.json) 是 HTTP 契约，直接编辑。接口页由它生成，地址是 `/api/<operationId>`。
- 代码块可以在语言后写标题和行高亮，例如 ` ```json title="POST /versions" {3,5} `。
- 页面历史从 Git 读取，未提交的改动和浅克隆都没有历史。

### 模型关系图

`<ModelRelations />` 读取 `content/docs/core/model/relations.json`，`<ObserverStructure node="…" />` 读取 `content/docs/observers/relations.json`。

- 节点 ID 使用完整的模型名，例如 `Observation`；成员节点使用“类型_成员名”，例如 `Observation_StartAt`。
- 每个节点的 `path` 指向模型页或规则锚点。修改 Core 页面路径或规则编号时，同步修改 `path`。
- Observers 的图复用 Core 节点的名称和链接。草案字段在标签中标明“草案”。

### 测试 Case 与报告

Case 页面在 `content/docs/testing/` 中，每页导出 `caseId` 并传给 `<TestCaseResults caseId={caseId} />`，测试用 `[Property("caseId", "…")]` 关联同一个 ID。

页面读取仓库根目录 `TestResults/` 中最新的 `Orisumi.Integration.Tests-*.tunit-report.json`，`ORISUMI_TEST_REPORTS_DIR` 可以换一个目录。页面不运行测试：先运行集成测试，开发服务器刷新即可，`pnpm build` 则把这一次的报告固定在产物中。完整报告在 `/testing/reports/integration`。

### Markdown 导出

| 地址 | 内容 |
| --- | --- |
| `/<页面路径>.md` | 单个页面 |
| `/llms.txt` | 页面索引 |
| `/llms-full.txt` | 全站内容 |

新增的组件需要 Markdown 形式时，在组件中处理 `asMarkdown()`，并加入 `components/markdown.tsx` 的 `getMarkdownComponents()`。

### Graph

`/start/graph` 展示页面之间的引用。收录哪些分区由 `lib/build-graph.ts` 的 `graphSections` 决定，`graphExcludedPages` 列出不进图的页面。

## 镜像

在仓库根目录构建，需要包含 `.git` 的完整 checkout：

```sh
docker build -f src/Docs/Orisumi.Docs/Dockerfile -t orisumi-docs:local .
```

镜像用 nginx 在 3000 端口提供 `out/`。构建上下文不包含 `TestResults`，镜像中的报告页显示报告尚未生成。
