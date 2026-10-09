# Heartbeat.Api

提供实体 HTTP 接口，组合 Application 操作与 Infrastructure 实现。职责见 [ADR 0005](../../Docs/Heartbeat.Docs/content/docs/adr/0005-backend-project-organization.mdx)，请求与响应见 [Heartbeat API](../../Docs/Heartbeat.Docs/content/docs/api/index.mdx)。

## 运行

本地开发见[根 README](../../../README.md#启动)和 [AppHost README](../../../tools/Heartbeat.AppHost/README.md)。Aspire 在 PostgreSQL 健康后运行独立迁移资源，迁移成功后启动本机 API。

直接运行需要 .NET 10 SDK，以及已应用项目迁移的 PostgreSQL 数据库。从仓库根目录设置连接配置并运行：

```sh
export ConnectionStrings__Heartbeat='Host=localhost;Port=54329;Database=heartbeat;Username=heartbeat;Password=heartbeat'
dotnet run --project src/Backend/Heartbeat.Api/Heartbeat.Api.csproj
```

服务启动时检查连接配置。建表由迁移程序完成，API 进程不自动应用迁移。

## 本地入口与代理

| 服务 | 本地入口 |
| --- | --- |
| API | <http://localhost:8080/api> |
| PostgreSQL | `127.0.0.1:54329` |
| 文档站 | <http://localhost:3000/core> |
| Aspire 看板 | <https://localhost:18888> |

NGINX 提供 8080 和 3000 两个入口。API 和文档站在本机运行，内部端口由 Aspire 分配并注入代理模板。8080 的 `/api/` 请求去掉前缀后转给 API，其他路径暂时返回 `404`。3000 的 `/api/entities/` 请求转给 API，其余请求原样转给文档站。模板见 [default.conf](../../../docker/nginx/default.conf)，职责见 [ADR 0006](../../Docs/Heartbeat.Docs/content/docs/adr/0006-unified-http-entry.mdx)。

NGINX 使用 `nginx:1.30.5-alpine`。容器启动时将 AppHost 注入的 `API_HOST`、`API_PORT`、`DOCS_HOST`、`DOCS_PORT` 写入配置，通过 Docker DNS 动态解析上游，缓存 30 秒。每个工作进程为每个上游保留最多 16 条空闲连接。

代理连接超时为 5 秒，读写间隔超时为 60 秒。NGINX 与 API 的请求体上限均为 10 MiB，超限返回 `413` 和 `application/problem+json`。

数据库名、用户名和开发密码均为 `heartbeat`。`migrate` 使用仓库固定的 EF 工具应用迁移后退出，无 HTTP 入口。停止 AppHost 保留数据库命名卷；文档站依赖和 Next.js 缓存在本机目录。

## 代码组织

`Program.cs` 负责依赖注册和路由组装。HTTP 入口使用 ASP.NET Core Minimal APIs，路由代码按业务目录组织。实体入口使用 `MapGroup` 声明 `/entities` 及其下级路径。

Api 中的保存请求类型只包含请求体属性。后端将路径 ID 与请求体组合为 Core 实体，再调用 Application 接口。

`Application/Entities/IEntityStore.cs` 定义四类保存方法和 `ReadAsync`。保存返回 `EntitySaveResult`，读取返回 `StoredEntity` 或 `null`。Infrastructure 的 `EntityStore` 实现接口并管理事务。

## OpenAPI

`Entities/` 中的端点和请求类型、`OpenApi/` 中的契约声明共同生成接口规范。从仓库根目录运行：

```sh
dotnet build src/Backend/Heartbeat.Api/Heartbeat.Api.csproj
```

输出为 [openapi.json](../../Docs/Heartbeat.Docs/content/docs/api/openapi.json)。该生成文件随代码提交，通过后端构建更新。

生成时跳过数据库注册，无需连接配置或 PostgreSQL 实例。文档站直接读取生成文件，启动和构建只需 Node 环境。

## Dockerfile

Dockerfile 先复制 SDK 配置、共享构建属性和项目文件，再还原依赖。EF 工具按 `.config/dotnet-tools.json` 还原。后续只复制 Core 和 Backend 源码，文档修改不会触发依赖还原。

Dockerfile 用于镜像构建。`build` 阶段发布 API 并生成 EF migration bundle；`final` 阶段使用 .NET 运行时，以非 root 用户运行 API，并保留 `/app/efbundle` 供独立迁移进程使用。镜像构建见 AppHost README，本地开发直接运行项目。

API 只提供 HTTP 接口，没有 `wwwroot`、Razor 或组件库静态文件，因此项目设置 `StaticWebAssetsEnabled=false`。这也避免 SDK 10.0.401 的 `dotnet watch` 在 Linux/macOS 上用混合路径分隔符读取 `staticwebassets.development.json` 时报错；[上游修复](https://github.com/dotnet/sdk/pull/54536)已合并，但当前 SDK 尚未包含。C# 热更新和 OpenAPI 生成照常运行。以后若 API 需要承载静态资源，应重新启用该功能并验证所用 SDK。

API 和迁移程序共用 `ConnectionStrings__Heartbeat`。PostgreSQL 初始化 SQL 位于 [Database/Docker/initialize.sql](../Heartbeat.Infrastructure/Database/Docker/initialize.sql)，数据库约定见 [Heartbeat Storage](../../Docs/Heartbeat.Docs/content/docs/storage/index.mdx)。

## 验证

HTTP 契约、实体读写和数据库行为由[集成测试](../../../tests/Heartbeat.Integration.Tests/README.md)验证。

修改开发构建或热更新配置时，使用独立 Aspire 环境、端口和数据卷验证 API。检查首次启动日志没有 `Failed to read ... staticwebassets.development.json`，然后修改已有处理方法的返回值，确认日志出现 `C# and Razor changes applied` 且 HTTP 响应变化。新增端点或修改启动注册需要完整重启进程。验证结束后停止独立环境并清理其验证卷，不操作日常开发数据库。
