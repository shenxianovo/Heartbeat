# Heartbeat.Web

Heartbeat.Web 是 Heartbeat 的 Web 控制台，只在本机使用。首页“现在”显示各 Observer 正在写入什么、今天记下了什么；从任何一个 Observer 或版本出发，可以进入实体页，沿引用逐层追溯。

控制台是读取方：它读取数据，也可以删除实体，写入由 Observer 通过 SDK 完成。领域模型以 [Core](../../Docs/Heartbeat.Docs/content/docs/core/index.mdx) 为准，HTTP 契约以 [API](../../Docs/Heartbeat.Docs/content/docs/api/index.mdx) 为准。

## 页面

| 路由                           | 页面                                   |
| ------------------------------ | -------------------------------------- |
| `/`                            | 现在：四种视图，见[现在](#现在)        |
| `/entities/$entityId?version=` | 实体：内容、引用链、版本和被引用       |
| `/versions/$versionId`         | 读取版本后跳到所属实体，并选中这个版本 |
| `/schemas`                     | EntitySchema 列表                      |

顶栏的输入框接受 EntityId 和 VersionId：先按 EntityId 读取，读不到再按 VersionId 读取。

## 现在

“现在”用四种视图展示同一份数据，`view` 参数选择视图，`observer` 参数是选中的 Observer。选中的 Observer 在切换视图后保持，其余的变淡。

| 视图 | `view`   | 显示什么                                                                                                    | 时间     |
| ---- | -------- | ----------------------------------------------------------------------------------------------------------- | -------- |
| 心电 | `ecg`    | 最近 20 秒的写入，指针从左向右扫；左上角是最近 60 秒的写入次数                                              | 保存时间 |
| 气泡 | `bubble` | 每个 Observer 一个气泡，面积随 10 分钟内的写入次数变化，同类别聚在一起                                      | 保存时间 |
| 行   | `strip`  | 每个活跃的 Observer 一行，最近 10 分钟的每次写入是一条竖线；安静的折叠成一行                                | 保存时间 |
| 表盘 | `ring`   | 今天按 24 小时展开，每个 Observer 一圈，每份 Observation 一段弧；超过 24 圈时，Observation 少的合并成“其他” | 事实时间 |

数据来自两组轮询：每 60 秒读一次今天的全部 Observation，每 2 秒读一次 `[now - 2 分钟, now + 1 分钟)` 的 Observation。两次轮询之间，一份 Observation 新出现或当前版本变了，算一次写入，画在它的保存时间上。所以写入在发生约 2 秒后出现，同一份 Observation 在一个轮询间隔内的多次更新算一次，事实时间远离今天的补发不出现。最近 60 秒内有写入的 Observer 算活跃。

Observer 是今天的 Observation 所引用的实体，名称取它的 Schema 版本的 `displayName`，同名的加 EntityId 末 4 位。颜色按类别区分，类别由 `src/lib/categories.ts` 中 Observer 的 `resourceName` 映射表决定，表中没有的归入“其他”。接入新的 Observer 时，在映射表里加一行。

视图不经过 React 渲染：`src/views/` 中的渲染函数每帧生成 SVG，`ActivityView` 写入页面。系统开启“减少动态效果”时，视图每秒重画一次，不播放动画。

## 演示数据

地址加 `?source=demo` 切到演示数据源，`?source=api` 切回后端；选择在当前标签页内保持，顶栏显示“演示数据”。演示数据源在浏览器内模拟约 14 个 Observer 今天的数据和持续的写入，包括一份带 InputSet 的推断和用户对它的否定，接口与后端相同。每次加载页面都重新生成，EntityId 也会变化。

## 外观

中性灰阶，有浅色和深色两套，定义在 `src/styles/globals.css`。主题有“系统、浅、深”三档，保存在浏览器的 localStorage 中，上次使用的视图也保存在这里。彩色只用于 Observer 的类别。

## 技术栈

React 19、TypeScript（strict）、Vite 7、TanStack Router、TanStack Query v5、TanStack Virtual、Tailwind CSS v4、d3-force、d3-shape、Vitest、Playwright。API 客户端按 `openapi.json` 手写，在 `src/api/`。

## 开发

本地开发通常用 `hb env up web` 启动，入口是 <http://localhost:8081/>。NGINX 把 `/api/` 转给 API，其他路径转给 Vite。

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

`npm run test:e2e:edge` 经本地环境的 Web 入口检查页面、同源 `/api`、HMR WebSocket，并通过 API 提交一个 Observer 和一份 Observation，确认它出现在各视图中并能打开实体页，结束后删除这些实体。先运行 `hb env up web`；用 `--temp` 时测试数据留在临时卷中。入口不是 8081 时，用 `WEB_URL` 指定。
