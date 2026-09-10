# Analytics Server

严格摄入 Segment 与 Input Event、以 snapshot upsert 持久化，并提供报表、Replay/Recap、
叙事知识与 App Catalog API 的 ASP.NET Core 服务。

## 目录

- `Program.cs`：composition root、认证、迁移与启动协调。
- `Controllers/`：HTTP 边界；端点真相源是 OpenAPI，不在这里复制路由表。
- `Services/`：ingest、report、recap/knowledge 与 App identity/catalog 模块。
- `Entities/`、`Data/`、`Migrations/`：持久模型与 schema 演进。
- `AppCatalog/`：部署全局产品目录及运行快照。

## 验证与归属

```bash
dotnet test server/Heartbeat.Server.Tests
```

数据库测试使用 Testcontainers，需要 Docker。本项目构建为 `heartbeat-backend` 容器镜像。
生产部署由 CI 独立执行 `dotnet Heartbeat.Server.dll --migrate`，成功后才启动 HTTP；
`--check-database` 和普通生产启动只检查版本，Development 仍自动迁移。
迁移 SQL 超时默认 0（无限等待），可用 `DatabaseMigration__CommandTimeoutSeconds` 设置有限秒数；
不改变普通请求超时。停写、备份和恢复见 [迁移 Runbook](../../docs/runbooks/analytics-database-migration.md)。
领域语义见 [Server Context](../CONTEXT.md)，API 见 [API 导读](../../docs/api.md)，数据库见
[数据库导读](../../docs/db.md)。
