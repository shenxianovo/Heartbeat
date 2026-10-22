# Heartbeat.Web

**重写约定：** 本前端的页面、交互、视觉和代码结构均可整体重写，无需兼容当前实现。领域模型与 HTTP 契约仍以文档站为准。

Heartbeat 控制台的独立 SPA。从任意实体 ID 出发，沿 `references` 展开观测主体（Observer）、观测（Observation）、实体定义（EntitySchema）与业务实体的关系网，并按事实时间回看。

- 领域模型与 HTTP 契约见文档站：`src/Docs/Heartbeat.Docs/content/docs/`（Core / API）。
- 后端只有单实体读写接口（`GET /entities/{id}` + 四类 `PUT`），没有列表/搜索；本应用以 ID 为入口，访问足迹与收藏的 Schema ID 存在浏览器 localStorage。

## 技术栈

React 19 + TypeScript(strict) · Vite 7 · TanStack Router / Query v5 · Tailwind CSS v4 · shadcn/ui（new-york 风格，手写组件）· Lucide · Vitest + Testing Library · Playwright。

## 视图

| 路由                         | 视图                                                                            |
| ---------------------------- | ------------------------------------------------------------------------------- |
| `/`                          | 首页：ID 检索、最近访问足迹、能力说明                                           |
| `/entities/$id?view=graph`   | 引用关系图（默认）：径向布局，点击虚线圈加载下一层引用                          |
| `/entities/$id?view=inspect` | 实体检视：类别、references、properties、观测事实时间、原始 JSON                 |
| `/entities/$id?view=pulse`   | Pulse：当前图中已加载观测的时间轴（点/区间/进行中渐隐/时间未知）                |
| `/compose`                   | 写入实体：选择类别、生成 UUIDv7、编辑 references/properties，实时契约校验后 PUT |

## 双生主题

两套配色各有浅/深色，用 `data-mascot="windows|mac"` + `.dark` 切换，定义在 `src/styles/globals.css`：

- **Windows 娘**（默认）：Azure 蓝、玻璃卡片、清醒好奇。
- **Mac 娘**：雾蓝银灰、品红心跳、安静柔和。

吉祥物图片在 `public/mascots/`（来源为仓库根 `assets/`）。

## 开发

```bash
npm install
npm run dev        # http://localhost:5173，/api 代理到 http://localhost:8080
```

通常通过 Aspire 启动整个本地环境，Web 控制台边缘入口为 <http://localhost:8081/>（nginx 同源转发 `/api` 到 API、`/` 到 Vite 并支持 HMR）。直连后端时入口为 8080；走 nginx 时 API 前缀为 `/api`（dev proxy 已去掉前缀）。

## 验证

```bash
npm run verify     # typecheck + eslint(--max-warnings=0) + prettier --check + vitest + build
npm run test:e2e   # Playwright（自动起 dev server；首次需 npx playwright install）
```

Aspire 边缘入口验证需要先 `aspire run` 起完整环境，再运行：

```bash
npm run test:e2e:edge   # ASPIRE_EDGE=1，校验 8081 页面、同源 /api 与 HMR WebSocket
```
