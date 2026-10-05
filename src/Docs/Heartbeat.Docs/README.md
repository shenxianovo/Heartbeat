# Heartbeat 文档站

使用 Fumadocs 与 Next.js。项目长期文档直接维护 [content/docs](content/docs)，阅读入口是 [index.mdx](content/docs/index.mdx)。

## 本地运行

```bash
pnpm install --frozen-lockfile
pnpm dev
```

访问 <http://localhost:3000/docs>。生产构建与启动使用 `pnpm build`、`pnpm start`；类型检查使用 `pnpm types:check`。

## 路径

Next.js 的 `basePath` 为 `/docs`。站点页面、静态资源、搜索 API 与 Markdown 导出都使用这个前缀。内部 Next.js 链接使用应用内路径，由框架添加前缀；直接 fetch、元数据及 Mermaid SVG 链接使用带前缀的公开路径。

模型正文只记录已确认的结构与语义，维护规则见仓库根目录的 [AGENTS.md](../../../AGENTS.md)。

Mermaid 图通过 `components/mermaid.tsx` 统一使用手绘风格。

模型关系统一维护在 `content/docs/model/relations.json`。图中节点对应独立模型页面。概览使用 `<ModelRelations />` 展示全部关系；模型节点使用 `<ModelRelations node="节点 ID" />` 展示与当前节点直接相连的关系。组件位于 `components/model-relations.tsx`。
