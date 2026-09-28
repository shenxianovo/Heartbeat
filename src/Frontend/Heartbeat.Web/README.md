# Heartbeat Web

Heartbeat 的概览与回放前端，使用 Next.js App Router、React 和 TypeScript。登录后的数据请求在浏览器中通过 TanStack Query 完成，Next.js 提供页面壳和到现有 .NET API 的同源转发。

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

| 环境变量                     | 用途                   | 默认值                          |
| ---------------------------- | ---------------------- | ------------------------------- |
| `HEARTBEAT_API_URL`          | Next.js 服务端转发目标 | `http://127.0.0.1:8080`         |
| `NEXT_PUBLIC_OIDC_AUTHORITY` | OIDC authority         | `https://auth.shenxianovo.com`  |
| `NEXT_PUBLIC_OIDC_CLIENT_ID` | 浏览器客户端 ID        | `heartbeat-web`                 |
| `NEXT_PUBLIC_OIDC_SCOPE`     | 登录 scope             | `openid profile offline_access` |

浏览器使用 OIDC Authorization Code + PKCE。OIDC 库的事务状态、access token 和 refresh token 都保存在 `sessionStorage`，退出登录会清除会话与查询缓存。回调地址是 `/auth/callback`，登录页是 `/login`，概览首页是 `/`，详细时间线是 `/timeline`。

本地登录需要认证服务允许回调 `http://localhost:3000/auth/callback` 和来源 `http://localhost:3000`。退出操作清除当前应用会话，不退出认证服务的单点登录会话。`NEXT_PUBLIC_*` 配置在构建时写入浏览器资源，修改后应重新构建。

API 数据结构以 [记录 HTTP 接口](../../../docs/recording-api.md) 为准。`value` 在公共类型中保持 `unknown`，前端不会把它作为 HTML 执行。

## 界面组件

前端使用 Tailwind CSS v4 和 shadcn/ui。`components.json` 指定 Radix Nova 组件及 `@/components/ui` 路径；新增组件在本目录运行 `npx shadcn add <组件名>`。生成的组件源码归本项目维护。

按钮统一使用 `src/components/ui/button.tsx` 的 `Button` 或 `buttonVariants`（用于保留链接语义）。默认按钮沿用现有玻璃样式；`glassPrimary`、`outline`、`ghost` 等变体都在同一处定义。颜色取自 `src/styles/tokens.css`。界面图标统一通过 `Icon` 使用 Lucide 的具名导入，保留尺寸和无障碍约定。

间距、字号和字重使用 Tailwind 默认刻度，主题颜色、圆角和阴影复用现有 token。简单布局使用 utility class；页面 CSS 保留专用网格和响应式规则。表单输入复用 `.field`，状态圆点复用 `.status-indicator`（`data-tone` 控制色彩），浮层外观复用 `.tooltip-surface`，各 Tooltip 自行处理定位与内容。

## 添加 Record 展示

1. 在 `registry.ts` 按 `(type, version)` 注册 `label` 和 `summarize`。摘要声明 Record 标签、可选子泳道 `group`、颜色语气 `tone` 和 `hover` 文案；需要专用详情时才提供 `Renderer`，否则使用安全 JSON。
2. 在 `src/components/records/renderers/` 实现协议值的检查与摘要。专用详情组件复用同一解析规则。

Track 的 `timeMode` 由通用时间轴映射为 Range 区间条或 Point 密度，协议展示代码不处理裁剪、布局、拖动或命中。页面和通用 Record 容器不需要修改。未知类型或版本使用安全的 JSON 展示；已知协议结构不匹配或 renderer 抛错时，明确标示单条记录解析失败并保留 JSON，其余记录继续显示。设计决策见 [ADR-0011](../../../docs/adr/ADR-0011-web-timeline-presentation-ownership.md)。

回放页面按 Owner、Track 和时间范围独立缓存查询结果。一条 Track 失败时其余 Track 继续展示，失败项可单独重试；Point 密度 tile 通过 QueryCache 订阅响应更新，刷新会使当前时间窗内各粒度缓存失效。页面选择状态与查询组合的责任见 [ADR-0012](../../../docs/adr/ADR-0012-web-replay-state-and-track-queries.md)。

## 绘图库边界

uPlot 专门用于高密度时序图；其他可视化按类型选择工具，不建立统一绘图库抽象。全天概览目前保留按数据变化计算的 SVG，拖动时只更新范围遮罩与手柄。

`useTimePlot` 只连接 React 与 uPlot 的生命周期、尺寸和主题：挂载创建、数据或视窗变化时批量同步、卸载销毁。它关闭库内的范围选择，沿用 Heartbeat 唯一的视窗与交互状态；两处 layout effect 分别同步外部图表数据和管理实例订阅，不推导页面状态。`RangePlot` 提供显式区间路径，`DensityCurve` 使用库内曲线路径；协议展示注册表继续只解释内容。绘制替换遵循 ADR-0011，不改变 API、Track 或 Record 语义。

## 验证

```bash
npm run verify
npx playwright install chromium
npm run test:e2e
```

`verify` 执行类型生成、TypeScript、ESLint、Prettier、Vitest 和生产构建。浏览器测试使用模拟认证和 API，不能代替真实链路验收；证据边界见[工程验证](../../../docs/verification.md)。

TypeScript lint 使用项目类型信息检查悬空 Promise 和 Promise 的错误使用。异步结果应等待或处理；明确交由库内部处理的调用及不等待返回值的事件入口用 `void` 表达意图。`void` 不会捕获拒绝，调用方仍需保证失败有对应处理。测试中的 React `act()` 也应等待。

真实链路在仓库根目录运行 `dotnet run --project tools/Heartbeat.Dev -- scenario desktop-replay`，连接桌面采集、Hub、后端与生产 Web，并在临时 Chromium 中使用真实 Auth 短期令牌会话核对本次 Record；`--interactive-login` 单独选择真实 OIDC 登录，`--recovery` 增加离线及重启恢复验收。前置条件与证据见[桌面应用到真实 Web 回放](../../../docs/verification.md#桌面应用到真实-web-回放)。

## 活动泳道拖动基准

用独立基准测量拖动时的渲染成本：

```bash
# 生产构建：评估实际交互性能
HEARTBEAT_PERF_MODE=production npm run test:perf
# 开发服务，用于阶段归因
npm run test:perf
```

基准用匿名、确定性数据模拟安静和忙碌的一天，测量整天视图的 `clamped-pan`、放大一次的 `wide-pan`、放大两次的 `zoomed-pan`，以及展开应用子泳道的情况。生产模式使用与 Docker 镜像相同的 standalone server。

报告按时间命名，保存在前端 `.artifacts/perf/`，命令会打印路径；可用 `HEARTBEAT_PERF_REPORT` 指定文件名。报告包含逐步耗时分位、浏览器长任务和阶段采样；生产模式另记录连续拖动的范围更新次数与帧间隔。`segments` 表示屏幕区间条数，稠密泳道由画布自报。

逐步测量会等待浏览器空闲，不能换算为帧率。退出码只表示测量是否完成，不判断性能是否达标。

读数方式：

- 生产构建用于评估交互性能。开发构建的 React 性能记录会放大成本，因此开发模式只测两组未展开数据；展开子泳道仅在生产模式测量，避免内存耗尽。`instability` 记录失稳事件，其后读数不可采信。
- 开发构建用于阶段归因。生产构建函数名已压缩，采样只能归入运行时分类。

## 回放约束

- 今天初始显示现在前后各一小时，边界处保持两小时并裁剪到当天。跟随时每 15 秒推进视窗并通过现有查询刷新数据；请求进行中跳过本次刷新，页面隐藏时暂停，重新可见后追上当前时间。
- 用户在时间线按下鼠标、滚动、使用键盘，或查看记录、记录分页时停止跟随；刷新和延迟返回的数据都不重置手动视窗。「回到现在」切回今天并恢复跟随，没有独立的跟随开关。跨午夜仅跟随状态切换日期。
- 历史日期在所选 Track 完整读取成功后聚焦最早活动前后各一小时；Point 轨道使用首个非空计数桶的位置，无活动时居中。首次读取期间用户已经操作则不再自动定位。自定义时间范围完整显示用户指定的窗口，不自动跟随。

- 顶层按 Collector 来源分组，每条 Track 独立展示；来源分组不表示 Device Identity。
- Range Track 完整读取后在浏览器裁剪，重叠区间分行，空白不补齐。
- 活动泳道的 Range 区间和 Point 密度统一用 uPlot / Canvas 2D 绘制；每条泳道（含展开的应用子泳道）只有一个图表，不再按记录数切换 DOM 分支。区间按颜色批量构建路径，重叠行高、行距与最小可点击宽度仍由 `rangeLayout.ts` 定义；最小像素宽度不改变 Record 的真实时间。
- 每条区间泳道是一个键盘入口，方向键浏览、回车打开当前记录；hover、点击和焦点标记使用同一时间几何。Point 密度保留真实桶的选择范围、跨泳道共享尺度，以及已读取的零计数与未观测空白的区别。
- Point Track 用服务端计数绘制密度，用户选择局部时间桶后才读取原始详情。
- 公共时间线只处理时间几何；协议摘要和详情由同一个 renderer registry 提供。
- 未知协议仍显示时间位置和安全的原始 JSON。
- 当前不计算 `range + next_record` 的派生结束时间。

已验收的交互与剩余边界见[回放体验验收](../../../docs/validation/experience-visualization.md)。

## Hub 管理

页头“Hub 管理”进入 `/hubs`，按当前 Owner 展示 Hub 联络、Collector 状态和 Record 交付。在线时可登录服务器已安装的 Collector：输入账号凭据、按需完成验证码，成功后自动开始采集；失效后重新登录自动恢复。账号身份由第三方认证结果确定。Desktop 仅展示状态，本地原生 UI 负责启停。行为权威见 [Hub 管理契约](../../../docs/hub-management.md)。

运行 `dotnet run --project tools/Heartbeat.Dev -- scenario hubs-fixture`（仓库根目录）验证首次登录、验证码错误重试、重新登录恢复、收发增量和离线禁用；该浏览器场景使用 mock 认证/API，不替代真实账号与原生客户端验收。

## 全局概览与下钻

首页 `/` 展示按 Collector 分开的时长气泡，提供 7/30/90 天和自定义范围。VRChat 气泡进入 `/vrchat`，桌面来源气泡进入筛选了该来源的 `/timeline`；底部「详细时间线」打开全部来源。页头只提供品牌首页与 Hub 管理，领域入口放在概览中；详情页有返回概览的路径。

时长来自已支持协议的显式 Range：桌面使用前台应用观测，VRChat 使用可见世界停留。同一 Collector 的区间裁剪到所选范围并取并集，避免重叠段重复计时；不同 Collector 独立展示，不累加成总时长，不做跨来源日志融合。点事件、未知协议和未知版本保留详细时间线入口，读取失败和解析失败明确展示，不能当成零活动。

`from`、`to`（ISO 时间）和 `collector` 查询参数把首页选择传给详情；不带参数的时间线保留今天自动跟随。概览和 VRChat 复用气泡布局，布局模块只处理尺寸与交互，协议解释仍在各视图投影内；展示与缓存责任沿用 ADR-0011/0012。浏览器场景同时检查首页空态/失败、概览到 VRChat、概览到泳道、来源/时间范围传递及窄屏布局。

## VRChat 世界与相遇

从首页的 VRChat 气泡进入 `/vrchat`，按单个 Collector 和时间范围展示世界气泡、访问明细、实例类型组成、星期/小时热力图及 API 可见好友同场。日期按浏览器本地时区；记录只算到已确认结束，不推到现在。不同来源不相加；断线可能将一次访问拆成多个观测段。数据语义和手动验收见 [VRChat 契约](../../Collectors/Heartbeat.Collector.VRChat/README.md) 与 [人工步骤](../../../docs/validation/vrchat-server-events.md)。
