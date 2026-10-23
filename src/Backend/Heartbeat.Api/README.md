# Heartbeat.Api

Heartbeat.Api 提供实体的 HTTP 接口。项目分工见 [ADR · 后端运行与本地环境](../../Docs/Heartbeat.Docs/content/docs/adr/backend-runtime.mdx#components)，接口契约见 [API](../../Docs/Heartbeat.Docs/content/docs/api/index.mdx)。

## 运行

本地开发通常通过 Aspire 启动，见 [AppHost README](../../../tools/Heartbeat.AppHost/README.md)。

单独运行 API 时，需要一个已应用迁移的 PostgreSQL 数据库。在仓库根目录运行：

```sh
export ConnectionStrings__Heartbeat='Host=localhost;Port=54329;Database=heartbeat;Username=heartbeat;Password=heartbeat'
dotnet run --project src/Backend/Heartbeat.Api/Heartbeat.Api.csproj
```

缺少 `ConnectionStrings__Heartbeat` 时，API 启动失败。API 不应用迁移。

## 遥测

API 只有在 `OTEL_EXPORTER_OTLP_ENDPOINT` 是包含主机名的 HTTP 或 HTTPS 绝对地址时，才导出日志、追踪和指标。没有配置或地址无效时，API 只输出控制台日志。

Aspire 会自动注入 OTLP 配置。在看板中选择 `api` 查看遥测。

单独运行时，自行配置接收器：

```sh
export OTEL_EXPORTER_OTLP_ENDPOINT='http://localhost:4317'
export OTEL_EXPORTER_OTLP_PROTOCOL='grpc'
export OTEL_SERVICE_NAME='heartbeat-api'
```

使用 HTTP/protobuf 接收器时，设置 `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`。导出器会在基地址后附加 `/v1/logs`、`/v1/traces` 和 `/v1/metrics`。没有设置服务名时，服务名是 `Heartbeat.Api`。

| 信号 | 范围 |
| --- | --- |
| 日志 | `ILogger`，保留结构化字段、格式化消息和日志作用域 |
| 追踪 | 入站 HTTP 请求和 Npgsql 调用。数据库 span 包含参数化 SQL，不包含参数值 |
| 指标 | HTTP 请求和 .NET 运行时 |

遥测不读取请求体，不启用 EF Core 敏感数据日志，也不记录 Npgsql 参数值。参考 [OpenTelemetry OTLP 导出器](https://github.com/open-telemetry/opentelemetry-dotnet/tree/core-1.20.0/src/OpenTelemetry.Exporter.OpenTelemetryProtocol)和 [Npgsql 追踪](https://www.npgsql.org/doc/diagnostics/tracing.html)。

## 代码组织

| 位置 | 内容 |
| --- | --- |
| `Program.cs` | 依赖注册和路由组装 |
| `Entities/` | 实体路由，使用 Minimal APIs 和 `MapGroup("/entities")` |
| `Heartbeat.Application/Entities/IEntityStore.cs` | 保存和读取接口 |
| `Heartbeat.Application/Entities/EntitySchemaRules.cs` | EntitySchema 声明和字段值的校验 |
| `Heartbeat.Infrastructure/Entities/EntityStore.cs` | 固定表和业务表的读写 |
| `Heartbeat.Infrastructure/Entities/BusinessTable.cs` | 业务表的建表和列映射 |

API 校验请求，把路径中的 ID 与 `references`、`properties` 组合成实体，再调用 `IEntityStore`。存储规则见 [Storage](../../Docs/Heartbeat.Docs/content/docs/storage/index.mdx)。

业务表使用动态 SQL：

- 表名和列名按标识符规则引用，值通过 Npgsql 参数传递。
- 数值以 JSON 原文写入 `numeric`。整数精确转换为 `bigint`。读取时由 PostgreSQL 生成 JSON，不经过浮点或 `decimal` 转换。

## 修改接口

1. 修改 [openapi.json](../../Docs/Heartbeat.Docs/content/docs/api/openapi.json)。
2. 修改 `Entities/` 中的实现。
3. 修改集成测试。

后端构建不生成也不覆盖 `openapi.json`。

## 镜像

```sh
docker build -f src/Backend/Heartbeat.Api/Dockerfile -t heartbeat-api:local .
```

Dockerfile 先复制 SDK 配置和项目文件并还原依赖，再复制 Core 和 Backend 源码。修改文档不会触发依赖还原。最终镜像使用 .NET 运行时，以非 root 用户运行，并包含 `/app/efbundle` 供独立的迁移进程使用。

## 已知问题

项目设置了 `StaticWebAssetsEnabled=false`。API 不提供静态文件，而 SDK 10.0.401 的 `dotnet watch` 在 macOS 和 Linux 上读取 `staticwebassets.development.json` 时会报路径分隔符错误。[上游修复](https://github.com/dotnet/sdk/pull/54536)已合并，但 SDK 10.0.401 尚未包含。API 需要提供静态文件时，重新启用这个功能，并验证所用 SDK。

## 验证

实体读写和数据库行为由[集成测试](../../../tests/Heartbeat.Integration.Tests/README.md)验证。

修改构建或热更新配置后，在独立的 Aspire 环境中验证：

1. 启动独立环境，见 [AppHost README · 验证](../../../tools/Heartbeat.AppHost/README.md#验证)。
2. 检查启动日志中没有 `Failed to read ... staticwebassets.development.json`。
3. 修改一个已有处理方法的返回值。
4. 检查日志出现 `C# and Razor changes applied`，并且 HTTP 响应随之变化。
5. 停止独立环境，删除它的验证卷。

新增端点或修改启动注册后，需要完整重启进程。
