# Orisumi.Web

Orisumi.Web 是 Orisumi 的 Web 控制台，只在本机使用。页面和视图的行为见 [Web](../../Docs/Orisumi.Docs/content/docs/clients/web.mdx)，HTTP 契约以 [API](../../Docs/Orisumi.Docs/content/docs/api/index.mdx) 为准。

## 页面

| 路由                           | 页面                                                                            |
| ------------------------------ | ------------------------------------------------------------------------------- |
| `/`                            | 现在，见 [Web · 现在](../../Docs/Orisumi.Docs/content/docs/clients/web.mdx#now) |
| `/entities/$entityId?version=` | 实体：内容、引用链、版本和被引用                                                |
| `/versions/$versionId`         | 读取版本后跳到所属实体，并选中这个版本                                          |
| `/schemas`                     | EntitySchema 列表                                                               |

## 接入新的 Observer

“现在”按 Observer 的类别着色，类别由 `src/lib/categories.ts` 中 `resourceName` 的映射表决定，表中没有的归入“其他”。接入新的 Observer 时，在映射表里加一行。

## 演示数据

地址加 `?source=demo` 切到演示数据源，`?source=api` 切回后端；选择在当前标签页内保持，顶栏显示“演示数据”。演示数据源在浏览器内模拟约 14 个 Observer 今天的数据和持续的写入，包括一份带 InputSet 的推断和用户对它的否定，接口与后端相同。每次加载页面都重新生成，EntityId 也会变化。

## 技术栈

React 19、TypeScript（strict）、Vite 7、TanStack Router、TanStack Query v5、TanStack Virtual、Tailwind CSS v4、d3-force、d3-shape、Vitest、Playwright。API 客户端按 `openapi.json` 手写，在 `src/api/`。

## 开发

本地开发通常用 `orisumi env up web` 启动，入口是 <http://localhost:8081/>。NGINX 把 `/api/` 转给 API，其他路径转给 Vite。

单独运行：

```sh
npm install
npm run dev
```

开发服务器地址是 <http://localhost:5173>，`/api` 代理到 <http://localhost:8080>。没有运行后端时，用 <http://localhost:5173/?source=demo> 查看。

## 验证

```sh
npm run verify      # 类型检查、ESLint、Prettier、Vitest 和构建
npm run test:e2e    # Playwright，自动启动开发服务器，用演示数据源，不连后端
```

首次运行 Playwright 前，执行 `npx playwright install chromium`。

`npm run test:e2e:edge` 经本地环境的 Web 入口检查页面、同源 `/api`、HMR WebSocket，并通过 API 提交一个 Observer 和一份 Observation，确认它出现在各视图中并能打开实体页，结束后删除这些实体。先运行 `orisumi env up web`；用 `--temp` 时测试数据留在临时卷中。入口不是 8081 时，用 `WEB_URL` 指定。
