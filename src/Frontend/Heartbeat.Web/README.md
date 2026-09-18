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

1. 在 `src/components/records/renderers/` 新增详情组件和概览摘要，共用协议 `value` 的检查与收窄逻辑。
2. 在 `registry.ts` 按 `(type, version)` 注册 `label`、`Renderer` 和 `summarize`。需要子泳道时提供稳定的 `group` 身份及显示名。

页面和通用 Record 容器不需要修改。未知类型、未知版本或结构不匹配的值会回退到安全的 JSON 展示；每条记录的 renderer 有独立错误边界。

## 验证

```bash
npm run verify
npx playwright install chromium
npm run test:e2e
```

`verify` 执行类型生成、TypeScript、ESLint、Prettier、Vitest 和生产构建。浏览器测试使用模拟认证和 API，不能代替真实链路验收；证据边界见[工程验证](../../../docs/verification.md)。

## 活动泳道拖动基准

拖动手感是渲染成本问题，靠肉眼判断不可靠，所以有一份独立的基准：

```bash
# 生产构建，看 Owner 实际拿到的手感
HEARTBEAT_PERF_MODE=production npm run test:perf
# 开发服务，用于阶段归因
npm run test:perf
```

基准用匿名的确定性数据模拟一天的量（安静一天约 100 条前台应用，忙碌一天 1,376 条前台应用、1,738 条前台窗口、175,492 个输入事件），分别测三种拖动：整天视图下拖不动的 `clamped-pan`、放大一次的 `wide-pan`、放大两次的 `zoomed-pan`，并额外测量展开应用子泳道的情况。生产模式启动与 Docker 镜像相同的 standalone server。每次报告以时间命名，保存在当前前端目录的 `.artifacts/perf/`，命令会打印完整路径；需要指定文件名时可设置 `HEARTBEAT_PERF_REPORT`。报告含每步耗时分位、浏览器长任务和按阶段归因的采样。退出码只说明基准是否完成，不表示速度合格。

两条读数纪律：

- 判断手感看生产构建。开发构建里 React 会把每次提交喂给自己的性能轨道，忙碌一天的单步成本被放大到秒级；开发模式只运行未展开的两组数据，展开子泳道只在生产模式测量，以免把开发标签页撑到内存耗尽。报告里的 `instability` 字段记录其他失稳事件，之后的读数不能采信。
- 阶段归因看开发构建。生产构建的函数名已压缩，采样只能落到运行时桶里。

## 回放约束

- 顶层按 Collector 来源分组，每条 Track 独立展示；来源分组不表示 Device Identity。
- Range Track 完整读取后在浏览器裁剪，重叠区间分行，空白不补齐。
- Point Track 用服务端计数绘制密度，用户选择局部时间桶后才读取原始详情。
- 公共时间线只处理时间几何；协议摘要和详情由同一个 renderer registry 提供。
- 未知协议仍显示时间位置和安全的原始 JSON。
- 当前不计算 `range + next_record` 的派生结束时间。

已验收的交互与剩余边界见[回放体验验收](../../../docs/validation/experience-visualization.md)。
