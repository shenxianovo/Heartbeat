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

打开 <http://localhost:3000/>，确认跳转到入口页 Introduction。

通过 Aspire 启动本地环境时，文档站已经由 NGINX 在 3000 端口提供，直接打开同一地址。

| 命令 | 作用 |
| --- | --- |
| `pnpm docs:check` | 检查项目文档的一致性，见[文档检查](#文档检查) |
| `pnpm types:check` | 生成路由类型并做类型检查 |
| `pnpm build` | 静态导出到 `out/`，再运行 `scripts/finish-export.mjs` 和 `scripts/check-export.mjs`，见[静态导出](#静态导出) |
| `pnpm test` | 运行 `tests/` 中的单元测试：测试 Case 与报告的关联、文档检查的解析逻辑 |

`next.config.mjs` 按运行阶段生成配置：开发服务器是普通的 Next.js 服务，构建时开启 `output: 'export'`。所以导出不支持的写法，例如依赖请求的路由，要到 `pnpm build` 时才报错。

## 静态导出

`pnpm build` 生成的 `out/` 是完整的站点，任何静态文件服务都能提供。页面、搜索索引、Markdown、LLM 导出和 OG 图都在构建时生成。

- `scripts/finish-export.mjs` 把每个页面的 Markdown 复制为 `/<页面路径>.md`，写入 `.nojekyll`，并把根页面写成跳转到 `/start/` 的页面。
- `scripts/check-export.mjs` 检查必要的页面和文件存在、搜索索引是 JSON、Markdown 内容正确，以及每个 HTML 中以 `/` 开头的站内链接和资源都有对应的文件。任何一项不通过，构建失败。
- 地址以 `/` 结尾，例如 `/core/`，`next.config.mjs` 设置了 `trailingSlash`。

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

发布后在公开站点检查：

- 首页跳转到 `/start/`，HTTPS 证书有效，不存在的地址显示 404 页面。
- 中文搜索和分区筛选。
- 模型关系图和 `/start/graph`。
- API 参考页，以及页面上没有 Playground。
- 测试报告和一个 Case 页面，报告的提交 SHA 是这次发布的提交。
- 一个页面的 `.md` 地址、`/llms.txt` 和 `/llms-full.txt`。

## 文档检查

`pnpm docs:check` 检查项目文档：文档站内容（包括 `openapi.json`）、仓库中每个 `README.md` 和 `AGENTS.md`，以及 `.agents/` 下的 skill。`src/` 和 `tests/` 下的 C# 代码也会检查，但只检查其中引用的规则编号。任何一项不通过，命令返回非零。

| 检查 | 内容 |
| --- | --- |
| `link` | 相对链接和站内绝对链接指向的文件存在。`/api/…` 是由契约生成的页面，不检查 |
| `anchor` | 锚点对应显式的 `[#id]`，或者标题按 github-slugger 生成的 slug |
| `rule-heading` | Core 规则标题是 `### 前缀-n 摘要 [#前缀-n]`，前缀属于本页，页内编号唯一且递增。前缀与页面的对应关系取自 Core Conventions 页的前缀表 |
| `rule-actor` | Core 规则标题下的第一段是 `定义`，或者 `执行方：` 加上 `后端`、`写入方`、`读取方` 中的一个或多个，用 `、` 分隔 |
| `rule-reference` | 文档和 C# 代码中出现的规则编号都存在 |
| `rule-link` | `[前缀-n](…)` 链接到这条规则所在的页面和锚点 |
| `deprecated-term` | 不出现 [Glossary · 不再使用的术语](content/docs/core/glossary.mdx) 中的术语 |
| `frontmatter` | 文档站页面都有 `title` 和 `description` |
| `adr-structure` | ADR 依次包含背景、决定、理由、代价、规则位置；流程类 ADR 可以在决定之后加“规则” |
| `json` | ` ```json ` 代码块可以解析，带标题或行高亮的代码块也检查 |
| `sidebar` | `meta.json` 中的条目存在，每个页面都被某个 `meta.json` 收录 |

检查逻辑在 `lib/docs-check.ts`，读取文件和输出在 `scripts/check-docs.ts`。

## 内容结构

| 分区 | 源文件 | 地址 |
| --- | --- | --- |
| Start | [content/docs/start](content/docs/start) | `/start` |
| Core | [content/docs/core](content/docs/core) | `/core` |
| Observers | [content/docs/observers](content/docs/observers) | `/observers` |
| Clients | [content/docs/clients](content/docs/clients) | `/clients` |
| API | [content/docs/api](content/docs/api) | `/api` |
| Storage | [content/docs/storage](content/docs/storage) | `/storage` |
| Testing | [content/docs/testing](content/docs/testing) | `/testing` |
| ADR | [content/docs/adr](content/docs/adr) | `/adr` |

- 每个分区的 `meta.json` 设置 `root: true` 和图标，Fumadocs 据此生成分区切换器。
- 侧边栏的顺序由 `meta.json` 的 `pages` 决定。`---标题---` 添加分组标题，`...目录` 展开一个目录。
- 访问 `/` 时跳转到 `/start`。
- 文档站按根路径运行，不配置 `basePath`。

写作约定见仓库根目录的 [AGENTS.md](../../../AGENTS.md) 和 [Core · Conventions](content/docs/core/conventions.mdx)。

## OpenAPI

[content/docs/api/openapi.json](content/docs/api/openapi.json) 是 HTTP 契约的权威来源，维护者直接编辑这个文件，见 [ADR · 文档的权威与传播](content/docs/adr/documentation-authority.mdx)。

- `lib/openapi.ts` 读取契约，`lib/source.ts` 用 `staticSource({ baseDir: 'api' })` 生成接口页，与 MDX 页面合并。
- 接口页地址是 `/api/<operationId>`。没有 `operationId` 时，使用路径和 HTTP 方法。
- 接口页使用 `components/api-page.tsx` 中的 `OpenAPIPage`。复制和导出提供原始 OpenAPI JSON。
- Playground 只在开发服务器上启用，使用相对服务地址 `/api`。通过 Aspire 访问时，3000 端口的 `/api/entities/…` 由 NGINX 转给后端，所以请求与文档站同源。

## Markdown 导出

每个页面都有 Markdown 版本，响应类型是 `text/markdown; charset=utf-8`。

| 地址 | 内容 |
| --- | --- |
| `/llms.mdx/<页面路径>/content.md` | 单个页面 |
| `/<页面路径>.md` | 同上。开发服务器由 `next.config.mjs` 改写到上一行的地址，构建时由 `scripts/finish-export.mjs` 生成文件 |
| `/llms.txt` | 页面索引 |
| `/llms-full.txt` | 全站内容 |

页面地址只返回 HTML。接口页的 Markdown 地址返回 OpenAPI JSON。路由定义在 `lib/routes.ts`，导出内容由 `lib/llms.ts` 生成。

- 指向文档页面的链接改写为页面的 Markdown 地址，例如 `../../adr/entity-model.mdx#context` 导出为 `/adr/entity-model.md#context`。页内锚点和外部链接保持原样。改写逻辑在 `lib/markdown-links.ts`。
- 正文按 Fumadocs 的 `output: 'function'` 生成：Markdown 原样保留，组件由 `components/markdown.tsx` 中的组件渲染。组件在 `asMarkdown()` 为真时返回 Markdown。
- `Callout` 导出为引用块，`Cards` 导出为链接列表。`ModelRelations` 导出关系列表，`ObserverStructure` 导出实现的接口和字段，`DocumentationGraph` 导出页面和引用清单。
- 测试报告组件不在 `components/markdown.tsx` 中，导出保留 MDX 标签。新增的组件需要 Markdown 形式时，在组件中处理 `asMarkdown()`，并加入 `getMarkdownComponents()`。

## 搜索

搜索索引由 `app/api/search/route.ts` 在构建时导出为 `/api/search`。浏览器下载索引，用 Fumadocs 的 Orama 静态客户端在本地查询。每个页面的索引带有所在分区的标签，取页面路径的第一段，例如 `core`、`adr`。

搜索框底部按侧栏分区筛选结果，再点一次取消筛选。分区列表在 `app/layout.tsx` 中从页面树生成，搜索框在 `components/search-dialog.tsx`。Fumadocs 默认的搜索框把底栏渲染在弹窗之外，筛选按钮会被遮罩挡住，所以这里把底栏放在弹窗内容中。

## 模型关系图

| 组件 | 数据 | 用法 |
| --- | --- | --- |
| `components/model-relations.tsx` | `content/docs/core/model/relations.json` | `<ModelRelations />` 显示全部关系，`<ModelRelations node="Observation" />` 只显示与一个节点直接相连的关系 |
| `components/observer-structure.tsx` | `content/docs/observers/relations.json` | `<ObserverStructure node="APPLICATION_CONTENT" />` 左侧显示实现的接口，右侧显示实体自己的字段 |

`relations.json` 的约定：

- 节点 ID 使用完整的模型名，例如 `Entity`、`Observation`、`BusinessEntity`。成员节点使用“类型_成员名”，例如 `Observation_StartAt`。
- 每个节点的 `path` 指向模型页或规则锚点。修改 Core 页面路径或规则编号时，同步修改 `path`。
- Observers 的图复用 Core 节点的名称和链接。字段只在 `observers/relations.json` 中维护一次。草案字段在标签中标明“草案”。
- 概览图使用 Dagre 布局。组件中有一条不可见的连线，只用来对齐两个分组，不表示模型关系。

所有 Mermaid 图由 `components/mermaid.tsx` 渲染。

## 测试 Case 与报告

Case 页面在 `content/docs/testing/` 中，每页导出 `caseId` 并传给 `<TestCaseResults caseId={caseId} />`。测试用 `[Property("caseId", "…")]` 关联同一个 ID。

- 组件读取最新的报告，只显示 `caseId` 精确匹配的结果，并单独计算这个 Case 的统计。
- 每条结果链接到 `?test=<测试 ID>`，在完整报告中打开详情。
- 没有报告或没有匹配结果时，页面显示没有执行证据。

完整报告页是 `/testing/reports/integration`，使用 `fumadocs-test-reports@0.1.2`。

报告目录由 `ORISUMI_TEST_REPORTS_DIR` 指定，没有设置时读取仓库根目录的 `TestResults`。组件选择修改时间最新的 `Orisumi.Integration.Tests-*.tunit-report.json`。文件不存在时，页面显示报告尚未生成。格式错误时，页面显示错误。报告没有提交 SHA 时，使用环境变量 `GITHUB_SHA`。

- 开发服务器每次请求都重新读取报告。页面不运行测试，先运行集成测试，再刷新页面。
- `pnpm build` 读取一次报告，结果固定在产物中。公开站点的报告由发布工作流运行集成测试生成，见[发布](#发布)。

## 页面历史

MDX 页面末尾用 [`fumadocs-page-history@0.4.2`](https://github.com/shenxianovo/fumadocs-page-history) 显示贡献者和提交历史。插件在 `source.config.ts` 中注册，中文翻译在 `lib/translations.ts` 中注册。

- 历史在构建时从 Git 读取，支持文件改名。未提交的文件没有历史。浅克隆需要先运行 `git fetch --unshallow`。
- 新的提交需要重新构建才会显示。开发期间只有 Git 记录变化时，需要重启文档站。
- `authorMappings` 把 Git 作者 `shenxianovo` 关联到同名 GitHub 账号，提供头像和主页链接。不请求 GitHub API。
- 日期在服务端按 UTC 渲染，浏览器加载后按读者的本地时区显示。
- `workspaceChanges` 当前为 `false`，正文不标记未提交的变化。
- 发布工作流用完整的 Git 历史检出，见[发布](#发布)。
- OpenAPI 生成的接口页没有页面历史。

## 布局

- 页面使用 Fumadocs 的 [Spacious 布局](https://www.fumadocs.dev/docs/ui/layouts/spacious)，样式在 `app/global.css` 中引入。
- 右侧页内目录使用 `clerk` 样式，配置在 `app/(docs)/[[...slug]]/page.tsx`。
- 站点名称和 Git 仓库信息在 `lib/site-config.ts`。编辑链接指向 `main` 分支。

### 正文组件

- Introduction 用 `Cards` 和 `Card` 按任务组织阅读链接，用 `Callout` 提示重写状态。这些组件来自默认 MDX 组件。
- Walkthrough 和 Integration Guide 的编号标题由 `source.config.ts` 中的 `remarkSteps` 渲染为 Steps。标题继续使用 `## 1. 名称 [#anchor]`，原有锚点和导出中的编号保持不变。
- Steps 在 Markdown 导出中保留编号标题。Cards 和 Callout 的导出形式见 [Markdown 导出](#markdown-导出)。
- 代码块可以在语言后写标题和行高亮，例如 ` ```json title="POST /versions" {3,5} `。行号从代码块的第一行算起。行高亮由 `source.config.ts` 中的 `transformerMetaHighlight` 处理，JSON 示例不需要写注释，仍能通过 `json` 检查。

### Graph

Start 分区的 `/start/graph` 展示 Core、ADR 和 Observers 的页面引用。节点按分区着色，箭头指向被引用的页面。点击节点打开文档；下方的页面列表提供普通链接。

- `lib/source.ts` 开启 `extractLinkReferences`，使用 Fumadocs 的链接解析按来源页面的目录处理相对路径。图只包含选定分区的页面，章节引用归到所在页面，重复的同向引用合并，页内引用忽略。
- 收录哪些分区由 `lib/build-graph.ts` 的 `graphSections` 决定，按侧栏分区路径的第一段填写，顺序就是图例的顺序。分区名称取侧栏标题，颜色按顺序从同一文件的色板分配，图例、节点颜色、悬停说明和页面列表都由此生成。写错的分区名会在构建时报错。
- `graphExcludedPages` 列出不进图的页面，按 URL 前缀匹配，当前排除 Document Template 的示例页。
- `lib/build-graph.ts` 整理节点、引用和 Markdown 清单。`components/documentation-graph.tsx` 在服务端生成图数据和页面列表。
- `components/graph-view.tsx` 改编自 [Fumadocs Graph View](https://github.com/fuma-nama/fumadocs/blob/main/apps/docs/components/graph-view.tsx)，保留其 MIT 许可。图形库在浏览器中加载，画布跟随容器尺寸变化，支持拖动、缩放和适应画布。节点按被引用的页面数决定大小：被引用最多的页面取最大半径，没有被引用的页面取最小半径，其余按引用数的平方根插值，面积大致与引用数成正比。最大和最小半径是 `components/graph-view.tsx` 中的 `maxRadius` 和 `minRadius`，页面增多时视觉比例不变。悬停时显示被引用和引用的页面数。
- Graph 页的 Markdown 导出包含每个页面的链接及其引用清单，由 `DocumentationGraph` 在导出时输出。单页和全站导出使用同一份图数据。

## 镜像

在仓库根目录构建，需要包含 `.git` 的完整 checkout：

```sh
docker build -f src/Docs/Orisumi.Docs/Dockerfile -t orisumi-docs:local .
```

- 构建阶段通过 BuildKit 只读挂载 `.git`，用于生成页面历史，然后运行 `pnpm build`。
- 运行阶段用 nginx 提供 `out/`，配置在 `nginx.conf`，监听 3000 端口，以非 root 用户运行。
- 构建上下文不包含 `TestResults`，所以镜像中的报告页显示报告尚未生成。
