# Heartbeat.Api

提供实体 HTTP 接口，组合 Application 操作与 Infrastructure 实现。职责见 [ADR 0005](../../Docs/Heartbeat.Docs/content/docs/adr/0005-backend-project-organization.mdx)，请求与响应见 [Heartbeat API](../../Docs/Heartbeat.Docs/content/docs/api/index.mdx)。

## 运行

容器启动和开发模式见[根 README](../../../README.md#启动)。Compose 在 PostgreSQL 健康后运行迁移程序，迁移成功后启动 API。

直接运行需要 .NET 10 SDK，以及已应用项目迁移的 PostgreSQL 数据库。从仓库根目录设置连接配置并运行：

```sh
export ConnectionStrings__Heartbeat='Host=localhost;Port=54329;Database=heartbeat;Username=heartbeat;Password=heartbeat'
dotnet run --project src/Backend/Heartbeat.Api/Heartbeat.Api.csproj
```

服务启动时检查连接配置。建表由迁移程序完成，API 进程不自动应用迁移。

## 容器操作

从仓库根目录运行：

```sh
docker compose ps -a
docker compose logs --tail=100 api migrate
docker compose logs --tail=100 docs
docker compose down
```

开发模式执行这些命令时，同样指定 `-f compose.yaml -f compose.dev.yaml`。两种模式共用项目名、端口和数据库卷；切换前先停止当前模式。

| 服务 | 本地入口 |
| --- | --- |
| API | <http://localhost:8080/api> |
| PostgreSQL | `127.0.0.1:54329` |
| 文档站 | <http://localhost:3000/core> |

NGINX 监听 8080 和 3000。API 和文档站只在容器网络内监听。8080 的 `/api/` 请求去掉前缀后转给 API，其他路径暂时返回 `404`。3000 的 `/api/entities/` 请求转给 API，其余请求原样转给文档站。配置见 [default.conf](../../../docker/nginx/default.conf)，职责见 [ADR 0007](../../Docs/Heartbeat.Docs/content/docs/adr/0007-unified-http-entry.mdx)。

NGINX 使用 Docker DNS 动态解析上游，解析结果缓存 30 秒。API 或文档站重建后，代理自动更新地址。上游连接可复用，连接超时为 5 秒，读写间隔超时为 60 秒。NGINX 与 API 的请求体上限均为 10 MiB，超限返回统一的 JSON `413` 错误。

数据库名、用户名和密码均为 `heartbeat`。`migrate` 应用迁移后退出，无 HTTP 入口。

`down` 保留数据卷。数据库位于 `postgres-data` 卷，开发依赖与 pnpm 缓存位于 `docs-node-modules` 卷，Next.js 缓存位于 `docs-next` 卷。`down -v` 删除当前配置中的命名卷及其内容。

## OpenAPI

`Entities/` 中的端点和请求类型、`OpenApi/` 中的契约声明共同生成接口规范。从仓库根目录运行：

```sh
dotnet build src/Backend/Heartbeat.Api/Heartbeat.Api.csproj
```

输出为 [openapi.json](../../Docs/Heartbeat.Docs/content/docs/api/openapi.json)。该生成文件随代码提交，通过后端构建更新。

生成时跳过数据库注册，无需连接配置或 PostgreSQL 实例。文档站直接读取生成文件，启动和构建只需 Node 环境。

## Dockerfile

Dockerfile 先复制 SDK 配置、共享构建属性和项目文件，再还原依赖。EF 工具按 `.config/dotnet-tools.json` 还原。后续只复制 Core 和 Backend 源码，文档修改不会触发依赖还原。

`development` 阶段保留 SDK 和依赖，由 `dotnet watch` 运行。`build` 阶段发布 API 并生成 EF migration bundle。`final` 阶段使用 .NET 运行时，以非 root 用户运行 API，同时提供 `/app/efbundle` 给 Compose 的 `migrate` 服务。

API 和迁移程序共用 `ConnectionStrings__Heartbeat`。PostgreSQL 初始化 SQL 位于 [Database/Docker/initialize.sql](../Heartbeat.Infrastructure/Database/Docker/initialize.sql)，数据库约定见 [Heartbeat Storage](../../Docs/Heartbeat.Docs/content/docs/storage/index.mdx)。

## 验证

HTTP 契约、实体读写和数据库行为由[集成测试](../../../tests/Heartbeat.Integration.Tests/README.md)验证。
