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

## App Catalog 管理约定

`/api/v1/admin/app-catalog/...` 族是部署全局的管理接口，只接受 JWT `sub` 出现在
`Administration:Subjects` 白名单中的用户。Dashboard 是否显示入口由 `/me.isAdmin`
决定，但 UI 隐藏不是安全边界：inventory、audit、Override preview/commit/delete 与
export 每个 action 都独立执行服务端管理员检查。username 不参与授权。

主要行为入口如下；参数和响应字段仍以 OpenAPI 为准：

| 行为 | 端点 |
| --- | --- |
| 产品、identity、有效来源、Override 与聚合使用量 | `GET /admin/app-catalog` |
| 最近 Catalog/Override 审计 | `GET /admin/app-catalog/audit` |
| 预览 identity 映射 | `POST /admin/app-catalog/overrides/{identityKey}/preview` |
| 创建或更新 Override | `PUT /admin/app-catalog/overrides/{identityKey}` |
| 预览删除后的 Catalog/provisional 回落 | `POST /admin/app-catalog/overrides/{identityKey}/delete-preview` |
| 删除 Override 并立即回落 | `DELETE /admin/app-catalog/overrides/{identityKey}` |
| 导出完整候选 Catalog | `POST /admin/app-catalog/export` |

表中的路径相对于 `/api/v1`。`identityKey` 是原始平台证据，调用方应按 URL path segment
编码。所有写操作先预览再确认；preview 与 commit 走同一领域协调逻辑，preview 的事务
最终回滚，不写 Override、audit 或产品映射。稳定领域错误使用
`AppCatalogAdminErrorResponse { code, message }`，常见 HTTP 语义为：验证错误 400、目标或
Override 不存在 404、映射冲突/rollback compatibility 409、非管理员 403。

Inventory 和 audit 是管理员诊断表面。普通 `/apps`、Report、Timeline 与
`/users/{username}` 公共 DTO 不暴露 raw AppIdentity、Override 或 provisional 分类内部状态。
Inventory 的使用信息只返回跨 Owner 聚合计数/时长/设备数与最后观测时间，不返回 Owner
subject、设备名、窗口标题或 segment 内容。

### 候选 Catalog 导出

Export request 只提交管理员明确选择的 identity keys；服务端只接受对应的 **active**
Override。返回值是 typed JSON envelope，其中 `content` 是候选文件的原始 UTF-8 bytes
（JSON 传输表现为 base64），`fileName` 为
`app-catalog.v{version}.candidate.json`，同时返回 SHA-256 `contentHash`。前端必须直接解码
这些 bytes 下载，不能把对象 parse 后再次 stringify。

候选文件只含 `schemaVersion`、`catalogVersion`、产品 `key`/`displayName` 与排序后的
`identities`。它不含数据库 Id、Owner 数据、使用量、管理员 sub、时间、图标或审计。
版本恒为当前随 binary 发布的正式 Catalog 版本加一；正式版本部署前重复导出保持同一
proposed version。选择没有造成内容变化时返回 typed `hasChanges=false`，不产生文件。

导出是纯读取，不更新 applied state、Override、audit 或映射；rollback-compatible 模式下
拒绝导出。服务端没有 Catalog JSON import/upload 端点。候选进入仓库与发布的完整流程见
[development guide](development.md#promoting-an-override-into-the-repository)。

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
