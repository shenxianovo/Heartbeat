# App Catalog Operations

本 runbook 用于修改内置 Catalog、配置部署管理员、把本地 Override 提升进仓库，以及诊断
Catalog 协调状态。领域契约见
[ADR-038](../adr/038-server-maintained-app-catalog.md)；API 形状以 OpenAPI 和 Controller 为准。

## 真相源

| 内容 | 真相源 |
| --- | --- |
| 内置产品与平台身份映射 | `server/Heartbeat.Server/AppCatalog/app-catalog.json` |
| Catalog 领域契约 | ADR-038 |
| 管理 API | `server/Heartbeat.Server/Controllers/AdminAppCatalogController.cs` 与 OpenAPI |
| 启动协调行为 | `server/Heartbeat.Server/AppCatalog/` |

Catalog 随 server binary 发布。Compose、环境变量和 frontend 不维护第二份 Catalog。

## 修改内置 Catalog

优先从管理页面导出候选文件，而不是手写重排 JSON。提交前确认：

- `schemaVersion` 只在 JSON 结构变化时增加。
- Catalog 内容变化时增加 `catalogVersion`；同版本不同内容会被视为 hash drift 并阻止启动。
- 产品按 canonical `key` 排序，每个产品的 `identities` 按 ordinal 排序。
- key 和 identity 已规范化，且不存在空值或重复映射。
- 文件使用 canonical serializer 的属性顺序、UTF-8 编码和末尾换行。

运行契约与协调测试：

```powershell
dotnet test server/Heartbeat.Server.Tests --filter FullyQualifiedName~AppCatalogLoaderTests
dotnet test server/Heartbeat.Server.Tests --filter FullyQualifiedName~AppCatalogReconcilerTests
dotnet test server/Heartbeat.Server.Tests
```

完成标准：测试通过，候选文件的 `catalogVersion` 比当前正式版本大一，diff 只包含经过核实的
产品和 identity 变化。

## 配置部署管理员

Catalog 映射影响所有 Owner，因此管理员身份使用不可变 JWT `sub`，不使用 username。
Compose 部署在 `.env` 中配置：

```dotenv
ADMIN_SUBJECT=the-auth-platform-jwt-sub
```

直接部署 ASP.NET Core 或配置多个管理员时使用索引配置：

```dotenv
Administration__Subjects__0=first-sub
Administration__Subjects__1=second-sub
```

修改后重启后端。`GET /api/v1/me` 的 `isAdmin` 只控制 UI 入口；每个管理端点仍会独立检查
服务端 subject 白名单。

完成标准：目标账号的 `/api/v1/me` 返回 `isAdmin: true`，普通账号访问管理端点得到 403。

## 把 Override 提升进仓库

Settings 页面创建的是部署本地 Override，不会修改内置 Catalog 或 `catalogVersion`。将已核实、
适用于所有部署的映射提升进仓库：

1. 只选择可以公开进入所有部署的 active Override；私有映射保持未选。
2. 导出 `app-catalog.v{N}.candidate.json`。候选是完整 Catalog 加所选映射，版本为当前正式版本加一。
3. 审查下载文件的原始 bytes，并独立核实每个原生 identity。
4. 用候选文件替换 `server/Heartbeat.Server/AppCatalog/app-catalog.json`。
5. 运行 Catalog 测试和完整 server 测试，通过代码审查后提交。
6. 部署后端；只有启动协调成功，版本 `N` 才会记录为已应用，对应 Override 才会进入 inactive 历史。

重复导出不会改变 Override、audit、产品映射或 applied state。系统没有 Catalog JSON import 端点。

完成标准：新 binary 启动成功，`AppCatalogStates` 记录版本 `N`，对应 Override 不再 active。

## 启动和回滚语义

启动在 EF migration 与知识回填之后取得 PostgreSQL advisory transaction lock
`heartbeat.app-catalog`，再在一个事务中应用 Override、内置 Catalog、兼容引用和 audit。多个
后端副本会串行协调并收敛。

数据库版本比 binary 更新时，后端进入 `rollback-compatible`：保留数据库现有映射，暂停新的
Override 写入和候选导出，直到部署携带相同或更高版本的 binary。同版本不同 hash 不是回滚兼容，
而是无效内容漂移，启动会失败。

通过部署正确版本的 binary 恢复，不直接编辑 `AppCatalogStates`。

## 只读诊断

打开本地数据库：

```powershell
docker compose -f compose.local.yml --env-file .env.local exec db `
  psql -U heartbeat -d heartbeat
```

macOS/Linux：

```bash
docker compose -f compose.local.yml --env-file .env.local exec db \
  psql -U heartbeat -d heartbeat
```

```sql
-- 当前正式 artifact 与启动模式。
SELECT "SchemaVersion", "CatalogVersion", "ContentHash", "AppliedAt", "StartupMode"
FROM "AppCatalogStates";

-- 最近的协调与 Override 历史。
SELECT "Id", "EventType", "CatalogVersion", "ContentHash", "ActorSubject",
       "OccurredAt", "SummaryJson"
FROM "AppCatalogAudits"
ORDER BY "OccurredAt" DESC, "Id" DESC
LIMIT 50;

-- 当前部署本地意图。
SELECT o."Id", i."Key" AS "IdentityKey", o."TargetAppKey", o."Status",
       o."CreatedBySubject", o."UpdatedBySubject", o."UpdatedAt"
FROM "AppCatalogOverrides" o
JOIN "AppIdentities" i ON i."Id" = o."AppIdentityId"
WHERE o."Status" = 'active'
ORDER BY i."Key";

-- 检查原生 identity 当前归属。
SELECT i."Key" AS "IdentityKey", a."Id" AS "AppId", a."Key" AS "AppKey",
       a."DisplayName", a."IsProvisional"
FROM "AppIdentities" i
JOIN "Apps" a ON a."Id" = i."AppId"
WHERE i."Key" IN ('win:chrome', 'mac:com.google.chrome')
ORDER BY i."Key";
```

变更映射时使用管理 API。直接更新 `AppIdentity.AppId`、Catalog state 或 Override 行会绕过历史
兼容引用、图标、知识、缓存和 audit 的协调。
