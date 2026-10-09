# Heartbeat 文档站

使用 Fumadocs 与 Next.js。项目长期文档直接维护 [content/docs](content/docs)，Core 阅读入口是 [index.mdx](content/docs/core/index.mdx)。

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

各分区在自己的 `meta.json` 中设置 `root: true` 和图标，Fumadocs 根据页面树生成切换器。Core 包含概览、术语表和模型正文。Observers 包含主体与观测定义。Storage 包含实体索引、存储表和数据库约定。Testing 包含测试分工、框架和数据库工具。ADR 展示架构决策记录（Architecture Decision Record），标题保留编号。

Core 侧栏按“指南、基础契约、观测模型、关系模型”分组，页面直接显示在对应标题下。分组和顺序由 `content/docs/core/meta.json` 与 `content/docs/core/model/meta.json` 中的 `pages` 控制：`---标题---` 添加分组标题，`...目录` 展开该目录的导航项。

右侧页内目录采用 `clerk` 样式，通过 SVG 轨道显示标题层级并高亮当前阅读位置；配置位于 `app/(docs)/[[...slug]]/page.tsx` 的 `tableOfContent`。

## OpenAPI

接口结构以 `Heartbeat.Api` 的端点、请求类型和契约声明为准。后端构建将 OpenAPI 生成到 [content/docs/api/openapi.json](content/docs/api/openapi.json)，该文件作为生成产物提交，不直接编辑。`lib/openapi.ts` 读取规范，`lib/source.ts` 通过 `staticSource({ baseDir: 'api' })` 生成虚拟接口页，并与 MDX 正文合并。API 导航保留概览，自动列出生成的页面。

接口页使用 `components/api-page.tsx` 中的官方默认 `OpenAPIPage` 组件，样式在 `app/global.css` 中引入。页面地址为 `/api/<接口名>`；官方默认命名使用 `operationId`，未提供时使用接口路径与 HTTP 方法。

接口页的复制与导出提供原始 OpenAPI 规范 JSON，编辑链接指向后端的 `Entities/EntityEndpoints.cs`。搜索、页内目录和分享图片使用同一页面源中的标题与内容结构。

## 本地运行

需要 Node.js 24 LTS 和 pnpm 12.3.4。从本目录运行以下命令。`pnpm dev` 与 `pnpm build` 直接读取仓库中的 OpenAPI 文件。

```bash
pnpm install --frozen-lockfile
pnpm dev
```

访问 <http://localhost:3000/core>。生产构建与启动使用 `pnpm build`、`pnpm start`；类型检查使用 `pnpm types:check`。

修改接口后，从仓库根目录构建后端以更新规范：

```sh
dotnet build src/Backend/Heartbeat.Api/Heartbeat.Api.csproj
```

生成过程不连接数据库。提交接口改动时一并提交生成的 `openapi.json`，审查契约 diff。文档站展示最近一次生成的规范。

## 路径

文档站按根路径运行，不配置 Next.js `basePath`。页面、静态资源、搜索、Markdown 导出、分享图片和 Mermaid 链接直接使用根路径地址。

NGINX 分别监听业务/API 端口 8080 和文档端口 3000。文档端口的 `/api/entities/…` 转给后端，其余请求原样转给文档站。接口文档 `/api/…` 和搜索 `/api/search` 由文档站处理。Playground 使用 OpenAPI 声明的相对服务地址 `/api`，通过文档端口同源访问后端。

访问文档入口 `/` 时跳转到 `/core`。8080 的根路径暂时返回 `404`，供后续业务前端使用。两种 Compose 模式共用 [NGINX 配置](../../../docker/nginx/default.conf)。单独运行 `pnpm dev` 时仍按根路径访问；实体 API 转发由 Compose 中的 NGINX 提供。

Markdown 响应声明 `text/markdown; charset=utf-8`。Core 的“查看 Markdown”地址为 `/llms.mdx/<页面路径>/content.md`，API 的同类入口返回 OpenAPI JSON。搜索入口为 `/api/search`。

模型正文只记录已确认的结构与语义，维护规则见仓库根目录的 [AGENTS.md](../../../AGENTS.md)。

Mermaid 图通过 `components/mermaid.tsx` 统一使用手绘风格。

模型关系统一维护在 `content/docs/core/model/relations.json`。图中节点对应独立模型页面。概览使用 `<ModelRelations />` 展示全部关系；模型节点使用 `<ModelRelations node="节点 ID" />` 展示与当前节点直接相连的关系。组件位于 `components/model-relations.tsx`。

## 测试报告

报告页为 `/testing/reports/integration`，使用 `fumadocs-test-reports@0.1.1` 的中文 TUnit 报告组件。

服务器从 `HEARTBEAT_TEST_REPORTS_DIR` 指定的目录读取报告。未设置时，本地默认读取仓库根目录的 `TestResults`；Docker 默认读取只读挂载的 `/reports`。

每次请求选择文件修改时间最大的 `Heartbeat.Integration.Tests-*.tunit-report.json`，由包的解析器校验。文件不存在时显示尚未生成，格式错误时报告错误。

页面不运行测试。先在仓库根目录执行集成测试，再刷新页面。报告在运行时读取，既不提交到文档正文，也不打包进镜像。

## Docker

容器启动和开发模式见[根 README](../../../README.md#启动)，状态、日志和停止命令见[容器操作](../../Backend/Heartbeat.Api/README.md#容器操作)。

开发容器的 pnpm 缓存位于 `node_modules/.pnpm-store`，随 `docs-node-modules` 命名卷保留。重建镜像或删除容器后仍可复用；删除该卷后，下次安装会重新下载依赖。

Dockerfile 缓存 pnpm 依赖安装，构建时读取仓库中的 OpenAPI 文件。pnpm 构建缓存由 BuildKit 管理，与开发容器的命名卷分开。

开发与构建阶段使用 Node 24 LTS 和 pnpm 12.3.4。运行阶段采用 Next.js standalone，只保留 Node 与运行产物，并以非 root 用户运行。
