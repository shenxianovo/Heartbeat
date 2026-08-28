# Dashboard

Vue SPA：展示 Subject 状态、报表、Timeline/Replay 与 Recap，并承载叙事知识写回和
Hub-local 交互授权入口。

## 目录

- `src/main.ts`、`src/router/`、`src/views/`：应用入口、路由与页面。
- `src/api/`：OpenAPI client、SSE 与 Hub API adapter；不在 README 复制 endpoint。
- `src/composables/`：设备、状态与报表等数据域协调。
- `src/timeline/`、`src/segmentAdapters.ts`：Timeline/Replay 纯模型与 Source adapter。
- `src/knowledge/`、`src/teaching/`：Strand、Episode、Matcher 与确认流。
- `src/components/`：展示组件。

## 验证与归属

```bash
npm --prefix frontend test
npm --prefix frontend run build
```

Vite `dist/` 进入 `heartbeat-frontend` nginx 镜像。领域边界见
[Frontend Context](CONTEXT.md)，API 约定见 [API 导读](../docs/api.md)。
