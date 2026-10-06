# Heartbeat

忠实保留观测及其必要上下文，为未来提出的问题提供依据。

项目正在重写。重写完成前不部署，不兼容旧数据、旧客户端或旧接口。

## 文档

- [文档首页](src/Docs/Heartbeat.Docs/content/docs/core/index.mdx)
- [术语表](src/Docs/Heartbeat.Docs/content/docs/core/glossary.mdx)
- [基础契约](src/Docs/Heartbeat.Docs/content/docs/core/model/contracts/entity.mdx)
- [观测模型](src/Docs/Heartbeat.Docs/content/docs/core/model/observations/observation.mdx)
- [关系模型](src/Docs/Heartbeat.Docs/content/docs/core/model/relations/relation.mdx)
- [实体存储](src/Docs/Heartbeat.Docs/content/docs/storage/index.mdx)
- [设计决策](src/Docs/Heartbeat.Docs/content/docs/adr)
- [Agent 规则](AGENTS.md)

长期文档直接维护文档站源文件。文档站作为独立前端项目运行，使用根路径 `/`。对外访问路径由外部入口决定，当前不配置反向代理。

## 本地启动文档站

```bash
cd src/Docs/Heartbeat.Docs
pnpm install --frozen-lockfile
pnpm dev
```

访问 <http://localhost:3000/core>。
