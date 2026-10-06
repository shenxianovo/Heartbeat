# Heartbeat 文档站

使用 Fumadocs 与 Next.js。项目长期文档直接维护 [content/docs](content/docs)，Core 阅读入口是 [index.mdx](content/docs/core/index.mdx)。

文档页采用 Fumadocs Base UI 的 [Spacious 布局](https://www.fumadocs.dev/docs/ui/layouts/spacious)。布局与页面组件分别从 `fumadocs-ui/layouts/spacious`、`fumadocs-ui/layouts/spacious/page` 导入，样式在 `app/global.css` 中引入 `fumadocs-ui/css/generated/spacious.css`。桌面端正文在独立面板内滚动。

标题下方使用 Fumadocs 的文档分区切换器，包含五个同级分区：

| 分区 | 源文件 | 阅读入口 | 图标 |
| --- | --- | --- | --- |
| Heartbeat Core | [content/docs/core](content/docs/core) | `/core` | `Network` |
| Heartbeat API | [content/docs/api](content/docs/api) | `/api` | `Plug` |
| Heartbeat Storage | [content/docs/storage](content/docs/storage) | `/storage` | `Database` |
| Heartbeat Testing | [content/docs/testing](content/docs/testing) | `/testing` | `FlaskConical` |
| Heartbeat ADR | [content/docs/adr](content/docs/adr) | `/adr/0001-documentation-source-of-truth` | `ScrollText` |

各分区在自己的 `meta.json` 中设置 `root: true` 和图标，Fumadocs 根据页面树生成切换器。Core 包含概览、术语表和模型正文。Storage 包含实体索引、存储表和数据库约定。Testing 包含测试分工、框架、数据库隔离和公共工具。ADR 独立显示设计决策，标题保留编号。

Core 侧栏按“指南、基础契约、观测模型、关系模型”分组，页面直接显示在对应标题下。分组和顺序由 `content/docs/core/meta.json` 与 `content/docs/core/model/meta.json` 中的 `pages` 控制：`---标题---` 添加分组标题，`...目录` 展开该目录的导航项。

右侧页内目录采用 `clerk` 样式，通过 SVG 轨道显示标题层级并高亮当前阅读位置；配置位于 `app/(docs)/[[...slug]]/page.tsx` 的 `tableOfContent`。

## OpenAPI

接口契约维护在 [content/docs/api/openapi.json](content/docs/api/openapi.json)。`lib/openapi.ts` 读取规范，`lib/source.ts` 通过 `staticSource({ baseDir: 'api' })` 生成虚拟接口页，并与 MDX 正文合并。API 导航保留概览，自动列出生成的页面。

接口页使用 `components/api-page.tsx` 中的官方默认 `OpenAPIPage` 组件，样式在 `app/global.css` 中引入。页面地址为 `/api/<接口名>`；官方默认命名使用 `operationId`，未提供时使用接口路径与 HTTP 方法。

接口页的复制与导出提供原始 OpenAPI 规范 JSON，编辑链接指向 `content/docs/api/openapi.json`。搜索、页内目录和分享图片使用同一页面源中的标题与内容结构。

当前规范按模型提供四个保存接口：

| 路径 | 接口标识与文档页 |
| --- | --- |
| `PUT /entities/{id}` | `saveEntity`，`/api/saveEntity` |
| `PUT /entities/observations/{id}` | `saveObservation`，`/api/saveObservation` |
| `PUT /entities/observers/{id}` | `saveObserver`，`/api/saveObserver` |
| `PUT /entities/observation-schemas/{id}` | `saveObservationSchema`，`/api/saveObservationSchema` |

实体 ID 由路径提供，模型类别由路径声明，请求体直接放实体除 `id` 外的全部属性。首次创建成功返回 `201`，替换或重复提交已有实体成功返回 `204`，均无响应体。

四个保存接口分别引用 `Entity`、`Observation`、`Observer` 和 `ObservationSchema` 字段 Schema。内置模型完整定义属性类型和必填项，通用领域数据保留自由 JSON 属性结构；实体标识共用 UUIDv7 Schema。

按标识读取使用 `GET /entities/{id}`，接口标识为 `getEntity`，文档页为 `/api/getEntity`。读取成功返回 `200`，JSON 响应包含模型类别 `category` 和实体除 `id` 外的全部属性 `entity`。`entity` 中的属性结构与对应保存接口的请求体一致，实体 ID 由请求路径提供。

读取响应引用 `EntityResponse`，使用 `oneOf` 和各分支的 `category` 常量对应模型结构。观测和观测定义的已知时间边界在读取时统一使用以 `Z` 结尾的 UTC 字符串，未知值为 `null`。

读取时，标识不是有效的 UUIDv7 返回 `400`，实体不存在返回 `404`，后端遇到未预期错误返回 `500`。错误响应共用 `application/problem+json` 格式。

## 本地运行

```bash
pnpm install --frozen-lockfile
pnpm dev
```

访问 <http://localhost:3000/core>。生产构建与启动使用 `pnpm build`、`pnpm start`；类型检查使用 `pnpm types:check`。

## 路径

文档站使用 Next.js 默认的根路径，不配置 `basePath`。页面、静态资源、搜索 API、Markdown 导出和分享图片都直接使用根路径地址，不额外拼接站点前缀。对外访问路径交给外部代理；当前不配置代理。

访问 `/` 时自动跳转到 Core 概览 `/core`。API 概览为 `/api`，Storage 概览为 `/storage`，Testing 概览为 `/testing`。ADR 分区没有独立概览页，从 `/adr/0001-documentation-source-of-truth` 开始阅读。

Markdown 响应显式声明 `text/markdown; charset=utf-8`，避免浏览器错误猜测中文编码。回归检查：分别在 Core 与 API 页面打开“查看 Markdown”，确认链接为 `/llms.mdx/<页面路径>/content.md` 且内容可读取；Core 返回 UTF-8 Markdown，API 返回 OpenAPI JSON。搜索使用默认 `/api/search`，图标与模型图节点链接使用根路径。

模型正文只记录已确认的结构与语义，维护规则见仓库根目录的 [AGENTS.md](../../../AGENTS.md)。

Mermaid 图通过 `components/mermaid.tsx` 统一使用手绘风格。

模型关系统一维护在 `content/docs/core/model/relations.json`。图中节点对应独立模型页面。概览使用 `<ModelRelations />` 展示全部关系；模型节点使用 `<ModelRelations node="节点 ID" />` 展示与当前节点直接相连的关系。组件位于 `components/model-relations.tsx`。
