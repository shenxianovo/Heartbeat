# Heartbeat Web

Heartbeat 的回放前端，使用 Next.js App Router、React 和 TypeScript。登录后的数据请求在浏览器中通过 TanStack Query 完成，Next.js 提供页面壳和到现有 .NET API 的同源转发。

## 本地运行

需要 Node.js 22.22.2+、24.15.0+ 或 26+。安装依赖并启动开发服务：

```bash
npm ci
npm run dev
```

默认访问 <http://localhost:3000>。`/api/*` 会转发到 `http://127.0.0.1:8080`，可通过环境变量调整：

```bash
HEARTBEAT_API_URL=http://127.0.0.1:8080 npm run dev
```

支持以下配置，示例值见 [`.env.example`](.env.example)：

| 环境变量                             | 用途                   | 默认值                          |
| ------------------------------------ | ---------------------- | ------------------------------- |
| `HEARTBEAT_API_URL` 或 `BACKEND_URL` | Next.js 服务端转发目标 | `http://127.0.0.1:8080`         |
| `NEXT_PUBLIC_OIDC_AUTHORITY`         | OIDC authority         | `https://auth.shenxianovo.com`  |
| `NEXT_PUBLIC_OIDC_CLIENT_ID`         | 浏览器客户端 ID        | `heartbeat-web`                 |
| `NEXT_PUBLIC_OIDC_SCOPE`             | 登录 scope             | `openid profile offline_access` |

浏览器使用 OIDC Authorization Code + PKCE。OIDC 库的事务状态、access token 和 refresh token 都保存在 `sessionStorage`，退出登录会清除会话与查询缓存。回调地址是 `/auth/callback`，登录页是 `/login`，回放工作台是 `/`。

本地登录需要认证服务允许回调 `http://localhost:3000/auth/callback` 和来源 `http://localhost:3000`。退出操作清除当前应用会话，不退出认证服务的单点登录会话。`NEXT_PUBLIC_*` 配置在构建时写入浏览器资源，修改后应重新构建。

## 目录

- `src/app/`：App Router 页面、布局和全局 Provider。
- `src/auth/`：OIDC 配置、会话动作和受保护页面边界。
- `src/api/`：后端响应类型、Bearer 请求和 TanStack Query hooks。
- `src/components/replay/`、`filters/`、`records/`：工作台、筛选和 Record 展示。
- `src/components/records/renderers/`：按 `(type, version)` 组织的值展示组件。
- `src/components/ui/`：共享按钮、浮层、日历、来源无关的多选 Picker 和 SVG 图标；样式集中在 `src/styles/ui.css`，沿用 main 的 design tokens。

API 数据结构以 [记录 HTTP 接口](../../../docs/recording-api.md) 为准。`value` 在公共类型中保持 `unknown`，前端不会把它作为 HTML 执行。

## 添加 Record 展示

1. 在 `src/components/records/renderers/` 新增详情组件和概览摘要函数，共用该协议的 `value` 检查与收窄逻辑。
2. 在 `registry.ts` 为准确的 `(type, version)` 注册类型显示名 `label`、`Renderer` 和 `summarize`。摘要提供主标签、辅助提示及可选视觉状态；需要可展开的子泳道时提供 `group` 的稳定身份及显示名。应用分组使用平台原生身份，不按显示名合并。

页面和通用 Record 容器不需要修改。未知类型、未知版本或结构不匹配的值会回退到安全的 JSON 展示；每条记录的 renderer 有独立错误边界。

## 验证

```bash
npm run verify
npx playwright install chromium
npm run test:e2e
```

`verify` 依次执行路由类型生成、TypeScript、ESLint、Prettier、Vitest 和生产构建。端到端测试会自行启动一个隔离端口上的开发服务。

浏览器测试模拟认证服务与 API，验证 PKCE 登录回调和页面交互；实际认证服务的回调配置需要在本地联调时验证。后端真实 PostgreSQL 查询由仓库的 .NET 集成测试覆盖。

当前回放在一个按天窗口中对齐多个 Track，可组合选择来源：Range Track 自动完整翻页，同 Track 的重叠区间分行展示；Point Track 使用服务端计数密度并在点击桶后分页读取局部原始详情。公共时间视口只负责时间几何，协议展示模块统一提供概览和详情；未知类型仍显示时间位置和安全的原始 JSON。切换来源后不会保留已排除记录的旧详情。当前不计算 `range + next_record` 的派生结束时间。

## 当天经历与活动泳道

展示沿用 main 的「当天经历」交互，在当前 React 前端实现。顶层按 Collector 来源分组，内部展示 Track；前台应用可展开为各应用子泳道。当前视窗无记录的应用、Track 与 Collector 分组逐级隐藏，展开状态在视窗切换后保留。来源分组不等于跨 Collector 设备身份，当前没有旧 Object/Fact 关系或 Browser 关联推断。

每条 Track 独立展示，不把观测状态等 Track 投影或合并到其他泳道。`desktop.observation.status` 在当前视窗有记录时显示自己的泳道并进入区间记录列表，状态记录可点击查看详情。

时间线视觉采用 main 原有 `ActivityTimeline.vue` 的紧凑布局：灰底标签列与刻度、细活动条、小型全天概览，区间内容通过悬停提示和点击详情查看。筛选栏、日期导航和刷新等操作复用共享 `Button`；`DatePicker` 与 `MultiSelectPicker` 共用 `Popover` 的定位、外部关闭及 Escape 焦点恢复。日历支持方向键、Home/End、PageUp/PageDown 和「今天」。`MultiSelectPicker` 仅接收选项和值，不依赖 Collector 模型。

全天概览可拖选、拖动选区、调整两端；泳道支持拖动平移、滚轮缩放、Shift 拖选，按钮和键盘提供等价操作。日期、范围、详情和记录列表联动，范围最短一秒，边界由所选日期或自定义窗口约束。窄屏按实际绘图区宽度减少刻度，泳道网格与时间刻度共享位置。

全天 Range 只完整读取一次，视窗变化在浏览器中裁剪；高频 Point 不下载全天明细。全天概览使用较粗计数，视窗停稳 150 毫秒后查询该范围及合适粒度的密度。新密度尚未就绪或读取失败时明确显示状态，不把旧范围的计数套在新视窗。点击密度才分页读取对应桶的原始记录。下方列表统计当前范围内的区间记录，不将其描述为全部输入事件。

记录几何与交互由 `timeRange.ts`、`rangeLayout.ts`、`ActivityOverview.tsx` 和 `TimelineViewport.tsx` 负责；`TimelineLane.tsx` 消费协议摘要和可选分组，不解析业务 JSON。原始区间保持独立，重叠分行，空白不补齐。端到端证据及范围见 [可视化恢复验证](../../../docs/validation/experience-visualization.md)。
