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

API 数据结构以 [记录 HTTP 接口](../../../docs/recording-api.md) 为准。`value` 在公共类型中保持 `unknown`，前端不会把它作为 HTML 执行。

## 添加 Record 展示

1. 在 `registry.ts` 按 `(type, version)` 注册 `label` 和 `summarize`。摘要声明 Record 标签、可选子泳道 `group`、颜色语气 `tone` 和 `hover` 文案；需要专用详情时才提供 `Renderer`，否则使用安全 JSON。
2. 在 `src/components/records/renderers/` 实现协议值的检查与摘要。专用详情组件复用同一解析规则。

Track 的 `timeMode` 由通用时间轴映射为 Range 区间条或 Point 密度，协议展示代码不处理裁剪、布局、拖动或命中。页面和通用 Record 容器不需要修改。未知类型、未知版本或结构不匹配的值会回退到安全的 JSON 展示；每条记录的 renderer 有独立错误边界。设计决策见 [ADR-0011](../../../docs/adr/ADR-0011-web-timeline-presentation-ownership.md)。

回放页面按 Owner、Track 和时间范围独立缓存查询结果。一条 Track 失败时其余 Track 继续展示，失败项可单独重试；Point 密度 tile 通过 QueryCache 订阅响应更新，刷新会使当前时间窗内各粒度缓存失效。页面选择状态与查询组合的责任见 [ADR-0012](../../../docs/adr/ADR-0012-web-replay-state-and-track-queries.md)。

## 验证

```bash
npm run verify
npx playwright install chromium
npm run test:e2e
```

`verify` 执行类型生成、TypeScript、ESLint、Prettier、Vitest 和生产构建。浏览器测试使用模拟认证和 API，不能代替真实链路验收；证据边界见[工程验证](../../../docs/verification.md)。

真实链路在仓库根目录运行 `dotnet run --project tools/Heartbeat.Dev -- scenario desktop-replay`，连接桌面采集、Hub、后端与生产 Web，并在临时 Chromium 中经 OIDC 登录核对本次 Record。前置条件与证据见[桌面应用到真实 Web 回放](../../../docs/verification.md#桌面应用到真实-web-回放)。

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

- 顶层按 Collector 来源分组，每条 Track 独立展示；来源分组不表示 Device Identity。
- Range Track 完整读取后在浏览器裁剪，重叠区间分行，空白不补齐。
- 可见区间超过 400 条时，整条泳道画在一张画布上，不再为每条记录建一个 DOM 节点；hover、选择和方向键浏览都落在这张画布上，颜色语气和文案仍来自同一个 renderer registry。密集绘制不画斜纹与边框，行高、行距与命中范围由 `rangeLayout.ts` 一处给出，两种介质共用。
- Point Track 用服务端计数绘制密度，用户选择局部时间桶后才读取原始详情。
- 公共时间线只处理时间几何；协议摘要和详情由同一个 renderer registry 提供。
- 未知协议仍显示时间位置和安全的原始 JSON。
- 当前不计算 `range + next_record` 的派生结束时间。

已验收的交互与剩余边界见[回放体验验收](../../../docs/validation/experience-visualization.md)。

## Hub 管理

页头“Hub 管理”进入 `/hubs`，按当前 Owner 展示 Hub 联络、Collector 状态和 Record 交付。在线时可配置服务器已安装类型、启停及移除；Desktop 支持现有平台 Collector 的远程启停。离线节点可退役。行为权威见 [Hub 管理契约](../../../docs/hub-management.md)。

运行 `dotnet run --project tools/Heartbeat.Dev -- scenario hubs-fixture`（仓库根目录）验证通用表单、在线操作和离线禁用；该浏览器场景使用 mock 认证/API，不替代真实账号与原生客户端验收。
