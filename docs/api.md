# Heartbeat API 导读

本文只记录机器生成不了的调用约定。端点、参数和响应 schema 的唯一真相源是 OpenAPI 与
Controller，不在手写文档中维护端点清单。

## 真相源

| 想知道 | 去哪看 |
| --- | --- |
| 端点、参数和响应形状 | Development 后端的 `/openapi/v1.json` |
| 实现与授权标记 | `server/Heartbeat.Server/Controllers/` |
| 前端实际调用方式 | `frontend/src/api/` |
| App Catalog 操作流程 | [App Catalog runbook](runbooks/app-catalog.md) |

本地栈通过 nginx 代理 `/openapi/`；生产后端不暴露 OpenAPI。业务 API 基础路径是
`/api/v1`。

## 鉴权约定

所有业务端点使用 `Authorization: Bearer {token}`。服务端按 JWT `typ` 接受两种凭证
（见 [ADR-024](adr/024-oidc-jwt-authentication.md)）：

- Dashboard 通过 OIDC 授权码 + PKCE 获取 access token。
- Agent 使用 ApiKey 向 Auth 平台换取短期 session JWT。

Agent 请求额外携带 `X-Hardware-Id` 和 `X-Device-Name`，服务端通过
`(OwnerId, HardwareId)` 解析 Device。历史 `Authorization: ApiKey` 协议已经退役。

按 username 读取的 `/users/{username}/...` 端点受可见性门控制：private 用户对匿名或
他人返回 404，本人通过 JWT `sub` 读取不受影响，public 用户允许匿名读取。`/me` 端点始终
需要鉴权。完整身份语义见 [ADR-025](adr/025-multi-user-visibility-identity.md)。

## 调用方约定

- 查询 action 返回 `ActionResult<T>` 或 `Task<T>`，让 OpenAPI 生成响应 schema；返回
  `IActionResult` 会使 NSwag 生成无类型的 `Promise<void>`。
- Agent 上传端点保持幂等，以支持离线缓存重传。
- 前端 wrapper 默认调用生成的 client，不复制 DTO 或响应类型。
- Daily/Weekly Report 必须传完整的版本化 Local Calendar Window envelope；生成 client 把
  `Start` / `EndExclusive` 序列化为 UTC instant，不会丢失 civil 语义。Recap 的 owner/public GET
  与手写 SSE POST 也必须传同一个完整 day envelope；只有尚未迁移的知识发问 `date` 端点仍保留
  浏览器 offset，并在 `frontend/src/api/index.ts` 手工构造 query string。
  通用时刻范围查询可以直接使用 UTC。

## Recap 约定

读与生成按动词拆分（[ADR-042](adr/042-recap-streaming-generation.md)）：**生成只由
`POST /api/v1/recaps/daily/generate` 触发，`GET /api/v1/recaps/daily` 永不调用 LLM、永不写库。**
GET 的 `force` 参数已取消。读取的三种态用字段组合隐式表达，不设 `notGenerated` 布尔：

| 态 | 判据 | 前端表现 |
| --- | --- | --- |
| 空日 | `isEmpty = true` | "这一天没有记录"，不生成 |
| 有数据但从未生成 | `isEmpty = false` 且 `narrative == null` | owner 视角自动发起一次生成 |
| 有叙事 | `narrative != null` | 直接渲染，附 `generatedAt` / `model` |

`segmentStale`（段数据长出了缓存水位）与 `knowledgeStale`（相关知识已更新）是两个平铺的判脏位，
都只提示。水位阈值留在服务端——防轮询烧 token 的护栏不交给前端。owner 视角下
`narrative == null` 或 `segmentStale` 触发一次自动生成，`knowledgeStale` 只提示。

生成端点是 SSE（`text/event-stream`），**从 OpenAPI 描述中排除、不进 codegen**：NSwag 无法为流
生成有意义的签名。它的契约由本文与 `frontend/src/api/index.ts` 里的手写 wrapper 维持——
`fetch` + `ReadableStream` 手工解析帧（帧解析在 `frontend/src/api/sse.ts`），不用 `EventSource`，
因为后者只能 GET 且带不了 `Authorization` 头，而认证是 Bearer。

| 事件 | data | 客户端处理 |
| --- | --- | --- |
| `delta` | `{ "delta": "正文增量" }` | 原样追加 |
| `thinking` | `{ "thinking": "推理增量" }` | 思考模型的思考期只有它；滚动显示"正在思考"，不进正文 |
| `done` | `{ "recap": DailyRecapResponse }` | 与 GET 同一形状，收敛为最终状态 |
| `error` | `{ "message": "可读原因" }` | 生成域的失败（响应头已发出，502 不再可能） |
| `ping` | `{}` | 心跳，忽略——未知事件类型同样必须忽略 |

时限（[ADR-042](adr/042-recap-streaming-generation.md) §5/§9）：**判死线量的是静默，不是总时长**
——收到任何一帧（含 `thinking`）就重置，连续 60 秒没有任何帧才判死，整段兜底 600 秒。思考模型的
第一个正文 token 可能来得很晚（实测 `deepseek-v4-pro` 默认 effort 下达 175 秒），这不是故障；
思考成本用 `Recap__ReasoningEffort`（`default|low|high|max|none`）调。

HTTP 状态码只负责鉴权/参数类 4xx 与并发 409。Analytics 严格验证 day envelope 后生成包含
version/kind/LocalDate/timezone/完整 UTC bounds 的 WindowKey；缓存与生成锁都以
`(OwnerId, WindowKey)` 识别，同一 key 撞上直接 409 + 一句可读中文，不排队。Browser 的
`correlationIdentity` 只用于隔离迟到响应，绝不进入持久化身份。

## App Catalog 管理约定

`/api/v1/admin/app-catalog/...` 影响所有 Owner，只允许 JWT `sub` 位于
`Administration:Subjects` 白名单的部署管理员访问。`/me.isAdmin` 只控制 UI 入口，每个
管理 action 都必须重复执行服务端 subject 检查。

写操作先 preview 再 commit；两者使用相同的领域协调逻辑，preview 事务最终回滚。稳定领域
错误使用 `AppCatalogAdminErrorResponse { code, message }`。Inventory 和 audit 只提供部署级
诊断，不向普通 DTO 暴露 raw AppIdentity、Override 或 provisional 状态。

候选 Catalog 导出返回 typed JSON envelope，其中 `content` 是原始 UTF-8 bytes 的 base64
表示。前端直接解码这些 bytes 下载，不能 parse 后重新 stringify，否则会改变 canonical
文件和 hash。导出是纯读取，不修改 Override、audit、映射或 applied state。

完整发布和诊断步骤见 [App Catalog runbook](runbooks/app-catalog.md)。

## 客户端重新生成

修改服务端 DTO 或端点后，先按 [Development Guide](development.md) 启动本地栈。生成命令
需要 NSwag CLI；本机没有 `nswag` 时先安装与当前生成文件一致的版本：

```powershell
dotnet tool install --global NSwag.ConsoleCore --version 14.7.1
```

然后生成客户端：

```powershell
nswag openapi2tsclient /input:http://localhost:8080/openapi/v1.json /output:frontend/src/api/client.ts
```

检查生成 diff，并执行前端类型检查：

```powershell
cd frontend
npx vue-tsc -b
```

如果正在通过本地 Compose 验证前端，重建 frontend 服务：

```powershell
docker compose -f compose.local.yml --env-file .env.local up -d --build frontend
```

完成标准：生成文件只反映预期契约变化，`vue-tsc` 通过，受影响请求在本地栈返回预期的
typed response。
