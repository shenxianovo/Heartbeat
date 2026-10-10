# Heartbeat 文档站

文档站使用 Fumadocs 与 Next.js。项目长期文档统一维护在 [content/docs](content/docs)，Core 阅读入口是 [index.mdx](content/docs/core/index.mdx)。

文档页采用 Fumadocs Base UI 的 [Spacious 布局](https://www.fumadocs.dev/docs/ui/layouts/spacious)。布局与页面组件分别从 `fumadocs-ui/layouts/spacious`、`fumadocs-ui/layouts/spacious/page` 导入，样式在 `app/global.css` 中引入 `fumadocs-ui/css/generated/spacious.css`。桌面端正文在独立面板内滚动。

标题下方使用 Fumadocs 的文档分区切换器，包含六个同级分区：

| 分区 | 源文件 | 阅读入口 | 图标 |
| --- | --- | --- | --- |
| Heartbeat Core | [content/docs/core](content/docs/core) | `/core` | `Network` |
| Observers | [content/docs/observers](content/docs/observers) | `/observers` | `ScanEye` |
| Heartbeat API | [content/docs/api](content/docs/api) | `/api` | `Plug` |
| Heartbeat Storage | [content/docs/storage](content/docs/storage) | `/storage` | `Database` |
| Heartbeat Testing | [content/docs/testing](content/docs/testing) | `/testing` | `FlaskConical` |
| Heartbeat ADR | [content/docs/adr](content/docs/adr) | `/adr/0001-documentation-source-of-truth` | `ScrollText` |

各分区在自己的 `meta.json` 中设置 `root: true` 和图标，Fumadocs 根据页面树生成切换器。Core 包含概览、术语表和模型正文。Observers 包含主体、业务实体与实体定义。Storage 包含实体索引、存储表和数据库约定。Testing 包含测试分工、框架和数据库工具。ADR 展示架构决策记录（Architecture Decision Record），标题保留编号。

Core 侧栏按“指南、基础契约、观测模型”分组，页面直接显示在对应标题下。分组和顺序由 `content/docs/core/meta.json` 与 `content/docs/core/model/meta.json` 中的 `pages` 控制：`---标题---` 添加分组标题，`...目录` 展开该目录的导航项。

右侧页内目录采用 `clerk` 样式。SVG 轨道显示标题层级，并高亮当前阅读位置。配置位于 `app/(docs)/[[...slug]]/page.tsx` 的 `tableOfContent`。

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

生产构建使用 `pnpm build`，启动使用 `pnpm start`。类型检查使用 `pnpm types:check`。开发和构建均读取仓库中的 OpenAPI 文件。

修改接口时，将用户在讨论中确认的结果直接写入 `content/docs/api/openapi.json`。接口页展示确定的契约，不记录实现进度。实现、测试和接口说明文档按契约同步。代码和其他文档不得反向修改契约。提交时审查契约差异。客户端按同一契约手写实现。

## 路径

文档站按根路径运行，不配置 Next.js `basePath`。页面、静态资源、搜索、Markdown 导出、分享图片和 Mermaid 链接直接使用根路径地址。

NGINX 分别监听业务/API 端口 8080 和文档端口 3000。文档端口的 `/api/entities/…` 转给后端，其余请求原样转给文档站。接口文档 `/api/…` 和搜索 `/api/search` 由文档站处理。Playground 使用 OpenAPI 声明的相对服务地址 `/api`，通过文档端口同源访问后端。

访问文档入口 `/` 时跳转到 `/core`。8080 的根路径暂时返回 `404`，供后续业务前端使用。Aspire 向 [NGINX 模板](../../../docker/nginx/default.conf) 注入本机服务地址。单独运行 `pnpm dev` 时，按根路径访问文档站。Aspire 管理的 NGINX 负责转发实体 API 请求。

Markdown 响应声明 `text/markdown; charset=utf-8`。Core 的“查看 Markdown”地址为 `/llms.mdx/<页面路径>/content.md`，API 的同类入口返回 OpenAPI JSON。搜索入口为 `/api/search`。

模型正文只记录已确认的结构与语义，维护规则见仓库根目录的 [AGENTS.md](../../../AGENTS.md)。

Mermaid 图通过 `components/mermaid.tsx` 统一使用手绘风格。

Core 模型关系统一维护在 `content/docs/core/model/relations.json`。接口和实体节点链接到模型页面，属性和方法节点链接到所属契约的章节。`IEntity` 节点只显示接口名称，`Id` 与 `GetReferences()` 分别使用独立节点。概览使用 `<ModelRelations />` 展示全部关系；模型节点使用 `<ModelRelations node="节点 ID" />` 展示与当前节点直接相连的关系。组件位于 `components/model-relations.tsx`。

Observers 的主图集中维护在 `content/docs/observers/relations.json`，不在概览中展示。具体页面通过 `<ObserverStructure node="节点 ID" />` 引用当前对象，左侧展示实现的接口，右侧展示实体自身增加的字段。接口已定义的成员不重复展示，例如 `IEntity.Id`。字段节点只显示名称，类型和取值在正文中说明。接口名称和链接复用 Core 主图；字段只在 Observers 主图中维护一次。组件位于 `components/observer-structure.tsx`。实体定义页的图展示观测内容。Observation 保存事实时间。草案字段必须在图中标明“草案”。

## 测试报告

报告页为 `/testing/reports/integration`，使用 `fumadocs-test-reports@0.1.1` 的中文 TUnit 报告组件。

服务器从 `HEARTBEAT_TEST_REPORTS_DIR` 指定的目录读取报告。未设置时，本地默认读取仓库根目录的 `TestResults`；Docker 默认读取只读挂载的 `/reports`。

每次请求时，服务器选择文件修改时间最大的 `Heartbeat.Integration.Tests-*.tunit-report.json`。`fumadocs-test-reports` 的解析器校验文件。如果文件不存在，页面显示尚未生成；如果格式错误，页面报告错误。

页面不运行测试。先在仓库根目录执行集成测试，再刷新页面。报告在运行时读取，既不提交到文档正文，也不打包进镜像。

## Aspire 与 Docker

完整本地环境由 Aspire 管理，启动、日志和停止见 [AppHost README](../../../tools/Heartbeat.AppHost/README.md)。文档站使用本机 `pnpm dev`。Aspire 的 pnpm 集成按锁文件安装依赖。`node_modules` 与 `.next` 缓存在本机目录。独立运行文档站时可以使用前面的本地命令，但不要同时占用 NGINX 的 3000 端口。

Dockerfile 保留镜像构建能力，使用 BuildKit 缓存 pnpm 依赖安装，构建时读取仓库中的 OpenAPI 文件。构建阶段使用 Node 24 LTS 和 pnpm 12.3.4。运行阶段采用 Next.js standalone，只保留 Node 与运行产物。容器以非 root 用户运行。报告在容器运行时通过只读挂载提供，不打包进镜像。
