# Heartbeat.Web

Heartbeat.Web 是 Heartbeat 的 Web 控制台。从任意 EntityId 出发，沿 `references` 展开 Observer、Observation、EntitySchema 和业务实体之间的关系，并按事实时间回看观测。

这个前端可以整体重写，页面、交互、视觉和代码结构都不需要兼容当前实现。领域模型以 [Core](../../Docs/Heartbeat.Docs/content/docs/core/index.mdx) 为准，HTTP 契约以 [API](../../Docs/Heartbeat.Docs/content/docs/api/index.mdx) 为准。

后端目前只有单实体读写接口，没有列表和搜索。控制台以 EntityId 为入口，最近访问记录和收藏的 SchemaId 保存在浏览器的 localStorage 中。

## 技术栈

React 19、TypeScript（strict）、Vite 7、TanStack Router、TanStack Query v5、Tailwind CSS v4、shadcn/ui（new-york 风格）、Lucide、Vitest、Testing Library、Playwright。

## 页面

| 路由                         | 页面                                                            |
| ---------------------------- | --------------------------------------------------------------- |
| `/`                          | 首页：按 ID 查找，最近访问记录                                  |
| `/entities/$id?view=graph`   | 引用关系图（默认视图）。点击虚线圈加载下一层引用                |
| `/entities/$id?view=inspect` | 实体详情：类别、`references`、`properties`、事实时间和原始 JSON |
| `/entities/$id?view=pulse`   | 时间轴：已加载 Observation 的时间点、时段、进行中和未知时间     |
| `/compose`                   | 写入实体：选择类别，生成 UUIDv7，编辑字段，按契约校验后保存     |

## 主题

控制台有两套配色，每套都有浅色和深色模式，用 `data-mascot="windows|mac"` 和 `.dark` 切换。定义在 `src/styles/globals.css`。

| 主题               | 配色                   |
| ------------------ | ---------------------- |
| Windows 娘（默认） | Azure 蓝，玻璃质感卡片 |
| Mac 娘             | 雾蓝和银灰，品红色强调 |

吉祥物图片在 `public/mascots/`，来源是仓库根目录的 `assets/`。

## 开发

本地开发通常用 `hb env up` 启动，入口是 <http://localhost:8081/>。NGINX 把 `/api/` 转给 API，其他路径转给 Vite。

单独运行：

```sh
npm install
npm run dev
```

开发服务器地址是 <http://localhost:5173>，`/api` 代理到 <http://localhost:8080>。

## 验证

```sh
npm run verify      # 类型检查、ESLint、Prettier、Vitest 和构建
npm run test:e2e    # Playwright，自动启动开发服务器
```

首次运行 Playwright 前，执行 `npx playwright install chromium`。

验证 Aspire 入口时，先运行 `hb env up web` 启动 Web 控制台和 API，再运行：

```sh
npm run test:e2e:edge   # 检查 8081 页面、同源 /api 和 HMR WebSocket
```
