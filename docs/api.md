# Heartbeat API 导读

本文档只讲**机器生成不了的约定**。端点清单、参数、响应 schema 的唯一真相源是
OpenAPI 文档与 Controller 代码本身——不在这里复述,复述必漂移。

## 真相源

| 想知道 | 去哪看 |
| --- | --- |
| 有哪些端点、参数、响应形状 | Development 后端的 `/openapi/v1.json`(本地栈经 nginx 代理 `/openapi/`;生产不暴露) |
| 端点的实现与授权标记 | `server/Heartbeat.Server/Controllers/` |
| 前端实际怎么调 | `frontend/src/api/`(NSwag 生成的 `client.ts` + 手写 wrapper `index.ts`) |

基础路径:`/api/v1`。

## 鉴权约定

所有业务端点要求 `Authorization: Bearer {token}`。服务端同时接受两种 token
(按 JWT `typ` 路由,详见 [ADR-024](adr/024-oidc-jwt-authentication.md)):

- **OIDC access token** — Dashboard 经授权码 + PKCE 登录取得
- **Agent session JWT** — 桌面 Agent 用 ApiKey 在 Auth 平台换取

Agent 请求额外携带 `X-Hardware-Id` / `X-Device-Name`,服务端以
(OwnerId, HardwareId) 解析设备。历史上的 `Authorization: ApiKey` 方案已退役
([ADR-004](adr/004-apikey-header-authentication.md))。

按用户名分享的 `/users/{username}/...` 族施加**可见性门**（[ADR-025](adr/025-multi-user-visibility-identity.md)）：
用户默认 private，对匿名/他人一律 404（不泄露用户名存在性），仅本人（携带 JWT 且
`sub == User.Id`）可读。用户设为 public 后该族对匿名放行。本人视角的 `/me`
（GET 供给 + 读设置，PUT `/me/settings` 改可见性）要求鉴权。

## 调用方约定

- 端点按调用方分两类:**[前端]**(Dashboard 只读消费)与 **[客户端]**(Agent
  上传)。上传类端点设计为幂等,支撑离线缓存重传(ADR-008/018)。
- 查询端点的 action 一律用 `ActionResult<T>` 而非 `IActionResult`,否则 OpenAPI
  推不出响应 schema、NSwag 会生成 `Promise<void>`。
- 报表端点(`/reports/daily|weekly`)的 `date` 参数必须携带浏览器本地时区偏移,
  服务端 `DateRange.Day/Week` 依赖它确定"今天/本周"边界(见 `shared/CONTEXT.md`
  时间存储约定);因此前端这两个 wrapper 手拼 query string,其余 wrapper 直接用
  生成的 client。

## 客户端重新生成

```powershell
nswag openapi2tsclient /input:http://localhost:8080/openapi/v1.json /output:frontend/src/api/client.ts
```

完整流程(启动本地栈、类型检查、重建镜像)见 [docs/development.md](development.md)。
