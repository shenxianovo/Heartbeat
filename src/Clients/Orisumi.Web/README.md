# Orisumi.Web

Orisumi.Web 是 Orisumi 的 Web 控制台，只在本机使用。页面和视图的行为见 [Web](../../Docs/Orisumi.Docs/content/docs/clients/web.mdx)，HTTP 契约以 [API](../../Docs/Orisumi.Docs/content/docs/api/index.mdx) 为准。

## 页面

| 路由                             | 页面                                                                                                                          |
| -------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `/?date=`                        | [日](../../Docs/Orisumi.Docs/content/docs/clients/web.mdx#day)                                                                |
| `/entities?root=&type=`          | [实体](../../Docs/Orisumi.Docs/content/docs/clients/web.mdx#entities)：从 `root` 织图；没有 `root` 时是类型图和每个类型的实体 |
| `/entities/$entityId?version=`   | 实体页：内容、引用、版本和删除                                                                                                |
| `/observers`                     | [Observer](../../Docs/Orisumi.Docs/content/docs/clients/web.mdx#observers)                                                    |
| `/applications`                  | [应用](../../Docs/Orisumi.Docs/content/docs/clients/web.mdx#applications)                                                     |
| `/sessions?date=&session=&view=` | [会话](../../Docs/Orisumi.Docs/content/docs/clients/web.mdx#sessions)                                                         |
| `/hosts`                         | [托管](../../Docs/Orisumi.Docs/content/docs/clients/web.mdx#hosts)                                                            |
| `/versions/$versionId`           | 读取版本后跳到所属实体，并选中这个版本                                                                                        |

根路由读 `stack` 参数，所以任何页面都可以打开右侧的面板。

## 代码结构

| 内容       | 位置                                                                                                                |
| ---------- | ------------------------------------------------------------------------------------------------------------------- |
| 角色       | `src/lib/roles.ts`、`src/hooks/useTypes.ts`                                                                         |
| 一天的数据 | `src/lib/day.ts`、`src/hooks/useDay.ts`                                                                             |
| 记号和面板 | `src/components/token/`、`src/components/stack/`                                                                    |
| 时间线     | `src/components/day/Timeline.tsx`                                                                                   |
| 实体的名字 | `src/lib/titles.ts`                                                                                                 |
| 应用产品   | `src/lib/applications.ts`、`src/hooks/useApplications.ts`、`src/components/applications/`                           |
| 助手会话   | `src/lib/conversations.ts`（分组、配对和筛选，纯函数）、`src/hooks/useConversations.ts`、`src/components/sessions/` |
| 托管       | `src/api/management.ts`、`src/hooks/useManagement.ts`、`src/components/hosts/`                                      |
| 织图       | `src/lib/weave.ts`（生长、线束和排布，纯函数）、`src/hooks/useWeave.ts`（读取邻居）、`src/components/weave/`        |
| 侧栏       | `src/components/AppShell.tsx`；“现在”和写入状态来自 `src/hooks/useCurrent.ts`，与日页面共用同一份查询               |
| 页头和卡片 | `src/components/ui.tsx` 的 `PageHeader` 和 `Card`                                                                   |

主题存在 localStorage 的 `orisumi.theme`，写到 `<html data-theme>`。`src/styles/globals.css` 的表面分四层：page 是纸面，side 是侧栏，card 是卡片，panel 是卡片里的浅色填充；卡片的投影用 `shadow-card`，弹出层用 `shadow-float`。吉祥物出现在空状态、读取和读不到的提示里，侧栏和标签页图标用 `public/mascots/macos-128.png`。

## 实体的名字

事物的名字由 `GET /things` 和 `POST /things/read` 给出。Observation 在界面上的名字默认取第一个不是引用的文字字段，取不到时写类型名。Schema 自己没有文字字段时，在 `src/lib/titles.ts` 按 `resourceName` 登记取法。

## 应用产品

归属在 `src/lib/applications.ts`，是不依赖 React 的纯函数：`buildCatalog` 按当前理解中的线索归属把线索和本机应用归到产品。`useCatalog` 读出产品、线索归属和本机应用描述，交给它。

## 接入新的 Observer

Observer 页面按 Observer 的类别着色，类别由 `src/lib/categories.ts` 中 `resourceName` 的映射表决定，表中没有的归入“其他”。接入新的 Observer 时，在映射表里加一行。它的观测内容引用了新的事物类型时，在 `src/components/token/icons.ts` 给这个类型登记图标，没有登记的用方块。

## 技术栈

React 19、TypeScript（strict）、Vite 7、TanStack Router、TanStack Query v5、TanStack Virtual、Tailwind CSS v4、lucide-react、Vitest、Playwright。API 客户端按 `openapi.json` 手写，在 `src/api/`。

## 开发

本地开发通常用 `orisumi env up web` 启动，入口是 <http://localhost:8081/>。NGINX 把 `/api/` 转给 API，其他路径转给 Vite。

单独运行：

```sh
npm install
npm run dev
```

开发服务器地址是 <http://localhost:5173>，`/api` 代理到 <http://localhost:8080>。页面的数据都来自后端，所以查看页面前先用 `orisumi env up web` 启动本地环境。

## 验证

```sh
npm run verify      # 类型检查、ESLint、Prettier、Vitest 和构建
npm run test:e2e    # Playwright，自动启动开发服务器，用路由拦截代替后端，检查外壳、空状态和应用页的修改
```

首次运行 Playwright 前，执行 `npx playwright install chromium`。

`npm run test:e2e:edge` 经本地环境的 Web 入口用真实数据检查页面：

- 页面、同源 `/api` 和 HMR WebSocket。
- 写入一个 Observer 和一份 Observation，确认它在 Observer 页面有一行，点这一行能打开它的面板。
- 写入一个事物和引用它的时段，检查日页面的排行、两层面板、Esc 和类型图；再从这个事物开始织，左边长出时段，再长出 Observer，停在 Observer 上时路上的标签亮起。

结束后删除这些实体。先运行 `orisumi env up web`；用 `--temp` 时测试数据留在临时卷中。入口不是 8081 时，用 `WEB_URL` 指定。
