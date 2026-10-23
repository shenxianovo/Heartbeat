# Heartbeat.Docs

Heartbeat.Docs 是 Heartbeat 的文档站，使用 Fumadocs 和 Next.js。项目的长期文档都写在 [content/docs](content/docs)。

## 运行

前置条件：Node.js 24 LTS 和 pnpm 12.3.4。

在本目录运行：

```sh
pnpm install --frozen-lockfile
pnpm dev
```

打开 <http://localhost:3000/core>，确认 Core 概览页可以打开。通过 Aspire 启动时，NGINX 已占用 3000 端口，不要再单独运行 `pnpm dev`。

| 命令 | 作用 |
| --- | --- |
| `pnpm docs:check` | 检查项目文档的一致性，见[文档检查](#文档检查) |
| `pnpm types:check` | 生成路由类型并做类型检查 |
| `pnpm build` | 生产构建 |
| `pnpm start` | 运行生产构建，用 `PORT` 和 `HOSTNAME` 指定监听地址 |
| `pnpm test` | 运行 `tests/` 中的单元测试：测试 Case 与报告的关联、文档检查的解析逻辑 |

`pnpm start` 先把 `public` 和 `.next/static` 复制到 standalone 目录，再启动 Next.js 服务器。

## 文档检查

`pnpm docs:check` 检查项目文档：文档站内容（包括 `openapi.json`）、仓库中每个 `README.md` 和 `AGENTS.md`，以及 `.agents/` 下的 skill。任何一项不通过，命令返回非零。

| 检查 | 内容 |
| --- | --- |
| `link` | 相对链接和站内绝对链接指向的文件存在。`/api/…` 是由契约生成的页面，不检查 |
| `anchor` | 锚点对应显式的 `[#id]`，或者标题按 github-slugger 生成的 slug |
| `rule-heading` | Core 规则标题是 `### 前缀-n 摘要 [#前缀-n]`，前缀属于本页，页内编号唯一且递增。前缀与页面的对应关系取自 Core 首页的前缀表 |
| `rule-reference` | 出现的规则编号都存在 |
| `rule-link` | `[前缀-n](…)` 链接到这条规则所在的页面和锚点 |
| `deprecated-term` | 不出现 [Glossary · 不再使用的术语](content/docs/core/glossary.mdx) 中的术语 |
| `frontmatter` | 文档站页面都有 `title` 和 `description` |
| `adr-structure` | ADR 依次包含背景、决定、理由、代价、规则位置；流程类 ADR 可以在决定之后加“规则” |
| `json` | ` ```json ` 代码块可以解析 |
| `sidebar` | `meta.json` 中的条目存在，每个页面都被某个 `meta.json` 收录 |

检查逻辑在 `lib/docs-check.ts`，读取文件和输出在 `scripts/check-docs.ts`。

## 内容结构

| 分区 | 源文件 | 地址 |
| --- | --- | --- |
| Core | [content/docs/core](content/docs/core) | `/core` |
| Observers | [content/docs/observers](content/docs/observers) | `/observers` |
| API | [content/docs/api](content/docs/api) | `/api` |
| Storage | [content/docs/storage](content/docs/storage) | `/storage` |
| Testing | [content/docs/testing](content/docs/testing) | `/testing` |
| ADR | [content/docs/adr](content/docs/adr) | `/adr` |

- 每个分区的 `meta.json` 设置 `root: true` 和图标，Fumadocs 据此生成分区切换器。
- 侧边栏的顺序由 `meta.json` 的 `pages` 决定。`---标题---` 添加分组标题，`...目录` 展开一个目录。
- 访问 `/` 时跳转到 `/core`。
- 文档站按根路径运行，不配置 `basePath`。

写作约定见仓库根目录的 [AGENTS.md](../../../AGENTS.md) 和 [Core Overview · 规则的写法](content/docs/core/index.mdx)。

## OpenAPI

[content/docs/api/openapi.json](content/docs/api/openapi.json) 是 HTTP 契约的权威来源，维护者直接编辑这个文件，见 [ADR · 文档的权威与传播](content/docs/adr/documentation-authority.mdx)。

- `lib/openapi.ts` 读取契约，`lib/source.ts` 用 `staticSource({ baseDir: 'api' })` 生成接口页，与 MDX 页面合并。
- 接口页地址是 `/api/<operationId>`。没有 `operationId` 时，使用路径和 HTTP 方法。
- 接口页使用 `components/api-page.tsx` 中的 `OpenAPIPage`。复制和导出提供原始 OpenAPI JSON。
- Playground 使用相对服务地址 `/api`。通过 Aspire 访问时，3000 端口的 `/api/entities/…` 由 NGINX 转给后端，所以请求与文档站同源。

## Markdown 导出

每个页面都有 Markdown 版本，响应类型是 `text/markdown; charset=utf-8`。

| 地址 | 内容 |
| --- | --- |
| `/llms.mdx/<页面路径>/content.md` | 单个页面 |
| `/<页面路径>.md` | 同上，由 `proxy.ts` 改写 |
| `/llms.txt` | 页面索引 |
| `/llms-full.txt` | 全站内容 |

请求头 `Accept` 优先 Markdown 时，页面地址也返回 Markdown。接口页的 Markdown 地址返回 OpenAPI JSON。路由定义在 `lib/routes.ts`。

## 模型关系图

| 组件 | 数据 | 用法 |
| --- | --- | --- |
| `components/model-relations.tsx` | `content/docs/core/model/relations.json` | `<ModelRelations />` 显示全部关系，`<ModelRelations node="Observation" />` 只显示与一个节点直接相连的关系 |
| `components/observer-structure.tsx` | `content/docs/observers/relations.json` | `<ObserverStructure node="APPLICATION_CONTENT" />` 左侧显示实现的接口，右侧显示实体自己的字段 |

`relations.json` 的约定：

- 节点 ID 使用完整的模型名，例如 `IEntity`、`Observation`、`BusinessEntity`。成员节点使用“类型_成员名”，例如 `Observation_StartAt`。
- 每个节点的 `path` 指向模型页或规则锚点。修改 Core 页面路径或规则编号时，同步修改 `path`。
- Observers 的图复用 Core 节点的名称和链接。字段只在 `observers/relations.json` 中维护一次。草案字段在标签中标明“草案”。
- 概览图使用 Dagre 布局。组件中有一条不可见的连线，只用来对齐两个分组，不表示模型关系。

所有 Mermaid 图由 `components/mermaid.tsx` 渲染。

## 测试 Case 与报告

Case 页面在 `content/docs/testing/` 中，每页导出 `caseId` 并传给 `<TestCaseResults caseId={caseId} />`。测试用 `[Property("caseId", "…")]` 关联同一个 ID。

- 组件读取最新的报告，只显示 `caseId` 精确匹配的结果，并单独计算这个 Case 的统计。
- 每条结果链接到 `?test=<测试 ID>`，在完整报告中打开详情。
- 没有报告或没有匹配结果时，页面显示没有执行证据。

完整报告页是 `/testing/reports/integration`，使用 `fumadocs-test-reports@0.1.1`。

报告目录由 `HEARTBEAT_TEST_REPORTS_DIR` 指定。没有设置时，本地读取仓库根目录的 `TestResults`，Docker 镜像读取 `/reports`。每次请求时，服务器选择修改时间最新的 `Heartbeat.Integration.Tests-*.tunit-report.json`。文件不存在时，页面显示报告尚未生成。格式错误时，页面显示错误。

页面不运行测试。先运行集成测试，再刷新页面。

## 页面历史

MDX 页末尾使用 [`fumadocs-page-history@0.3.1`](https://github.com/shenxianovo/fumadocs-page-history) 显示贡献者和可折叠的提交历史。`source.config.ts` 注册 Remark 插件，`repository.url` 复用 `lib/site-config.ts` 的 Git 配置；样式在 `app/global.css` 引入，中文翻译在 `lib/translations.ts` 注册。日期在服务端使用 UTC，浏览器接管后自动使用读者的本地时区。`authorMappings` 将 Git 作者 `shenxianovo` 手动关联到同名 GitHub 账号，提供头像和个人主页链接。OpenAPI 自动生成的接口页不经过 MDX 编译，因此没有这个页尾。

历史在编译时从完整 Git checkout 读取，支持文件更名。未提交的新文件没有历史；已提交文件的未提交修改不产生历史条目。新增提交后重新构建；开发期间仅 Git 记录变化时需要重启文档站。浅克隆需先执行 `git fetch --unshallow`。默认不请求 GitHub API，也不需要 GitHub token。

Docker 构建必须从仓库根目录使用包含 `.git` 的完整 checkout。`Dockerfile.dockerignore` 保留构建所需的 `.git`；构建阶段安装 Git，按仓库相对路径放置文档，并通过 BuildKit 只读挂载 Git 数据。运行镜像只包含 Node 与站点产物。

## OpenAPI

接口使用 OpenAPI First：先确定契约，再编写实现。维护者直接修改 [content/docs/api/openapi.json](content/docs/api/openapi.json)。该文件是 HTTP 契约的权威来源。后端构建不生成或覆盖契约文件。`lib/openapi.ts` 读取规范，`lib/source.ts` 通过 `staticSource({ baseDir: 'api' })` 生成虚拟接口页，并与 MDX 正文合并。API 导航保留概览，自动列出生成的页面。

接口页使用 `components/api-page.tsx` 中的官方默认 `OpenAPIPage` 组件，样式在 `app/global.css` 中引入。页面地址为 `/api/<接口名>`。接口名默认使用 `operationId`。如果未提供 `operationId`，接口名使用接口路径与 HTTP 方法。

接口页的复制与导出提供原始 OpenAPI 规范 JSON，编辑链接指向契约源文件 `content/docs/api/openapi.json`。搜索、页内目录和分享图片使用同一页面源中的标题与内容结构。

## 本地运行

前置条件：安装 Node.js 24 LTS 和 pnpm 12.3.4。

从本目录运行以下命令：

```bash
pnpm install --frozen-lockfile
pnpm dev
```

验证：访问 <http://localhost:3000/core>，确认模型概览页面可以打开。

生产构建使用 `pnpm build`，本地启动使用 `pnpm start`。启动脚本先将 `public` 与 `.next/static` 复制到 standalone 目录，再运行 Next.js 生成的服务器；使用 `PORT` 和 `HOSTNAME` 环境变量指定监听地址。脚本在服务器切换工作目录前确定测试报告路径，默认仍读取仓库根目录的 `TestResults`。类型检查使用 `pnpm types:check`。Case 关联逻辑检查使用 `pnpm test`。开发和构建均读取仓库中的 OpenAPI 文件。

修改接口时，将用户在讨论中确认的结果直接写入 `content/docs/api/openapi.json`。接口页展示确定的契约，不记录实现进度。实现、测试和接口说明文档按契约同步。代码和其他文档不得反向修改契约。提交时审查契约差异。客户端按同一契约手写实现。

## 路径

文档站按根路径运行，不配置 Next.js `basePath`。页面、静态资源、搜索、Markdown 导出、分享图片和 Mermaid 链接直接使用根路径地址。

站点名称与 Git 仓库信息维护在 `lib/site-config.ts`，编辑链接指向 `main` 分支。文档、Markdown 导出与分享图片的路由维护在 `lib/routes.ts`；页面 URL 使用 Fumadocs 的 `createGetUrl` 生成，供页面链接与静态路由参数共用。

NGINX 分别监听业务/API 端口 8080 和文档端口 3000。文档端口的 `/api/entities/…` 转给后端，其余请求原样转给文档站。接口文档 `/api/…` 和搜索 `/api/search` 由文档站处理。Playground 使用 OpenAPI 声明的相对服务地址 `/api`，通过文档端口同源访问后端。

访问文档入口 `/` 时跳转到 `/core`。8080 的根路径暂时返回 `404`，供后续业务前端使用。Aspire 向 [NGINX 模板](../../../docker/nginx/default.conf) 注入本机服务地址。单独运行 `pnpm dev` 时，按根路径访问文档站。Aspire 管理的 NGINX 负责转发实体 API 请求。

Markdown 响应声明 `text/markdown; charset=utf-8`。Core 的“查看 Markdown”地址为 `/llms.mdx/<页面路径>/content.md`，API 的同类入口返回 OpenAPI JSON。搜索入口为 `/api/search`。

模型正文只记录已确认的结构与语义，维护规则见仓库根目录的 [AGENTS.md](../../../AGENTS.md)。

Mermaid 图通过 `components/mermaid.tsx` 统一使用手绘风格。

Core 模型关系统一维护在 `content/docs/core/model/relations.json`。接口和实体节点链接到模型页面，属性和方法节点链接到所属契约的章节。`IEntity` 节点只显示接口名称，`Id` 与 `GetReferences()` 分别使用独立节点。概览使用 `<ModelRelations />` 展示全部关系；模型节点使用 `<ModelRelations node="节点 ID" />` 展示与当前节点直接相连的关系。组件位于 `components/model-relations.tsx`。

Observers 的主图集中维护在 `content/docs/observers/relations.json`，不在概览中展示。具体页面通过 `<ObserverStructure node="节点 ID" />` 引用当前对象，左侧展示实现的接口，右侧展示实体自身增加的字段。接口已定义的成员不重复展示，例如 `IEntity.Id`。字段节点只显示名称，类型和取值在正文中说明。接口名称和链接复用 Core 主图；字段只在 Observers 主图中维护一次。组件位于 `components/observer-structure.tsx`。实体定义页的图展示观测内容。Observation 保存事实时间。草案字段必须在图中标明“草案”。

## 测试 Case 与报告

Testing 侧栏按能力组织独立 Case，首个样板地址为 `/testing/entity-schemas/table-creation-failure`。MDX 声明 `caseId` 并传给 `TestCaseResults`，测试使用 `[Property("caseId", "同一 ID")]` 引用。正文与执行证据分别维护；组件精确匹配最新原生报告的属性，保留所有参数化结果，重新计算此 Case 的展示统计。每条结果提供 `?test=<原生测试 ID>` 链接，在完整报告中打开该结果详情。无报告或没有匹配结果均显示没有执行证据。


报告页为 `/testing/reports/integration`，使用 `fumadocs-test-reports@0.1.1` 的中文 TUnit 报告组件。

服务器从 `HEARTBEAT_TEST_REPORTS_DIR` 指定的目录读取报告。未设置时，本地默认读取仓库根目录的 `TestResults`；Docker 默认读取只读挂载的 `/reports`。

每次请求时，服务器选择文件修改时间最大的 `Heartbeat.Integration.Tests-*.tunit-report.json`。`fumadocs-test-reports` 的解析器校验文件。如果文件不存在，页面显示尚未生成；如果格式错误，页面报告错误。

页面不运行测试。先在仓库根目录执行集成测试，再刷新页面。报告在运行时读取，既不提交到文档正文，也不打包进镜像。

## Aspire 与 Docker

完整本地环境由 Aspire 管理，启动、日志和停止见 [AppHost README](../../../tools/Heartbeat.AppHost/README.md)。文档站使用本机 `pnpm dev`。Aspire 的 pnpm 集成按锁文件安装依赖。`node_modules` 与 `.next` 缓存在本机目录。独立运行文档站时可以使用前面的本地命令，但不要同时占用 NGINX 的 3000 端口。

Dockerfile 保留镜像构建能力，使用 BuildKit 缓存 pnpm 依赖安装，构建时读取仓库中的 OpenAPI 文件。构建阶段使用 Node 24 LTS 和 pnpm 12.3.4。运行阶段采用 Next.js standalone，只保留 Node 与运行产物。容器以非 root 用户运行。报告在容器运行时通过只读挂载提供，不打包进镜像。
## 布局

- 页面使用 Fumadocs 的 [Spacious 布局](https://www.fumadocs.dev/docs/ui/layouts/spacious)，样式在 `app/global.css` 中引入。
- 右侧页内目录使用 `clerk` 样式，配置在 `app/(docs)/[[...slug]]/page.tsx`。
- 站点名称和 Git 仓库信息在 `lib/site-config.ts`。编辑链接指向 `main` 分支。

## 镜像

在仓库根目录构建，需要包含 `.git` 的完整 checkout：

```sh
docker build -f src/Docs/Heartbeat.Docs/Dockerfile -t heartbeat-docs:local .
```

- 构建阶段通过 BuildKit 只读挂载 `.git`，用于生成页面历史。
- 运行阶段使用 Next.js standalone 产物，以非 root 用户运行。
- 测试报告在运行时通过只读挂载提供，不打包进镜像。更新报告不需要重建镜像。
