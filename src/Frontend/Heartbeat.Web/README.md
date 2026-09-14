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

API 数据结构以 [记录 HTTP 接口](../../../docs/recording-api.md) 为准。`value` 在公共类型中保持 `unknown`，前端不会把它作为 HTML 执行。

## 添加 Record 展示

1. 在 `src/components/records/renderers/` 新增一个组件，由组件自行检查并收窄 `value`。
2. 在 `registry.ts` 为准确的 `(type, version)` 增加一条静态注册。

页面和通用 Record 容器不需要修改。未知类型、未知版本或结构不匹配的值会回退到安全的 JSON 展示；每条记录的 renderer 有独立错误边界。

## 验证

```bash
npm run verify
npx playwright install chromium
npm run test:e2e
```

`verify` 依次执行路由类型生成、TypeScript、ESLint、Prettier、Vitest 和生产构建。端到端测试会自行启动一个隔离端口上的开发服务。

浏览器测试模拟认证服务与 API，验证 PKCE 登录回调和页面交互；实际认证服务的回调配置需要在本地联调时验证。后端真实 PostgreSQL 查询由仓库的 .NET 集成测试覆盖。

当前回放只展示后端返回的 Track 级记录，不计算 `range + next_record` 的派生结束时间。
