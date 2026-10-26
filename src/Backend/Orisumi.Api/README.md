# Orisumi.Api

Orisumi.Api 是后端的 HTTP 宿主，提供 [API](../../Docs/Orisumi.Docs/content/docs/api/index.mdx) 中的实体接口。项目分工见 [ADR · 后端运行与本地环境](../../Docs/Orisumi.Docs/content/docs/adr/backend-runtime.mdx#components)，接口契约见 [openapi.json](../../Docs/Orisumi.Docs/content/docs/api/openapi.json)。

## 运行

本地开发通常通过 Aspire 启动，见 [AppHost README](../../../tools/Orisumi.AppHost/README.md)。

单独运行 API 时，需要提供已完成迁移的数据库的连接字符串。在仓库根目录运行：

```sh
export ConnectionStrings__Orisumi='Host=localhost;Port=54329;Database=orisumi;Username=orisumi;Password=orisumi'
dotnet run --project src/Backend/Orisumi.Api/Orisumi.Api.csproj
```

缺少 `ConnectionStrings__Orisumi` 时，API 启动失败。API 不应用迁移。

## 遥测

API 只有在 `OTEL_EXPORTER_OTLP_ENDPOINT` 是包含主机名的 HTTP 或 HTTPS 绝对地址时，才导出日志、追踪和指标。没有配置或地址无效时，API 只输出控制台日志。

Aspire 会自动注入 OTLP 配置。在看板中选择 `api` 查看遥测。

单独运行时，自行配置接收器：

```sh
export OTEL_EXPORTER_OTLP_ENDPOINT='http://localhost:4317'
export OTEL_EXPORTER_OTLP_PROTOCOL='grpc'
export OTEL_SERVICE_NAME='orisumi-api'
```

使用 HTTP/protobuf 接收器时，设置 `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`。导出器会在基地址后附加 `/v1/logs`、`/v1/traces` 和 `/v1/metrics`。没有设置服务名时，服务名是 `Orisumi.Api`。

| 信号 | 范围 |
| --- | --- |
| 日志 | `ILogger`，保留结构化字段、格式化消息和日志作用域 |
| 追踪 | 入站 HTTP 请求和 Npgsql 调用。数据库 span 包含参数化 SQL，不包含参数值 |
| 指标 | HTTP 请求和 .NET 运行时 |

遥测不读取请求体，不启用 EF Core 敏感数据日志，也不记录 Npgsql 参数值。参考 [OpenTelemetry OTLP 导出器](https://github.com/open-telemetry/opentelemetry-dotnet/tree/core-1.20.0/src/OpenTelemetry.Exporter.OpenTelemetryProtocol)和 [Npgsql 追踪](https://www.npgsql.org/doc/diagnostics/tracing.html)。

## 代码组织

| 位置 | 内容 |
| --- | --- |
| `Program.cs` | 依赖注册、请求体上限和错误处理 |
| `Telemetry.cs` | 日志、追踪和指标的注册 |
| `EntityEndpoints.cs` | 各接口的路由、查询参数和分页游标 |
| `SubmissionContract.cs` | 提交请求体的契约检查，不符合时整次提交都不处理 |
| `ApiProblem.cs` | problem+json 错误响应，以及规则编号到状态码的映射 |
| `Orisumi.Core` | 版本、三种 Core 类型的内容、EntitySchema 校验和引用枚举 |
| `Orisumi.Application/Entities/EntityService.cs` | 提交、读取、删除版本和列出引用方，执行后端规则；保存版本时整理它的引用，交给存储写入反查表 |
| `Orisumi.Application/Entities/IEntityStore.cs` | 存储的事务接口 |
| `Orisumi.Infrastructure/Database/` | PostgreSQL 实现、Initial migration 和时间转换，见 [Storage](../../Docs/Orisumi.Docs/content/docs/storage/index.mdx) |

## 修改接口

1. 修改 [openapi.json](../../Docs/Orisumi.Docs/content/docs/api/openapi.json)。
2. 修改 API 中的实现。
3. 修改集成测试。

后端构建不生成也不覆盖 `openapi.json`。

## 镜像

```sh
docker build -f src/Backend/Orisumi.Api/Dockerfile -t orisumi-api:local .
```

Dockerfile 先复制 SDK 配置和项目文件并还原依赖，再复制 Core 和 Backend 源码。修改文档不会触发依赖还原。最终镜像使用 .NET 运行时，以非 root 用户运行，并包含 `/app/efbundle` 供独立的迁移进程使用。

## 已知问题

项目设置了 `StaticWebAssetsEnabled=false`。API 不提供静态文件，而 SDK 10.0.401 的 `dotnet watch` 在 macOS 和 Linux 上读取 `staticwebassets.development.json` 时会报路径分隔符错误。[上游修复](https://github.com/dotnet/sdk/pull/54536)已合并，但 SDK 10.0.401 尚未包含。API 需要提供静态文件时，重新启用这个功能，并验证所用 SDK。

## 验证

模型规则由[模型测试](../../../tests/Orisumi.Model.Tests/README.md)验证，接口、存储和遥测由[集成测试](../../../tests/Orisumi.Integration.Tests/README.md)验证。

修改构建或热更新配置后，在独立的 Aspire 环境中验证：

1. 启动独立环境，见 [AppHost README · 验证](../../../tools/Orisumi.AppHost/README.md#验证)。
2. 检查启动日志中没有 `Failed to read ... staticwebassets.development.json`。
3. 修改一个已有处理方法的返回值。
4. 检查日志出现 `C# and Razor changes applied`，并且 HTTP 响应随之变化。
5. 停止独立环境，删除它的验证卷。

新增端点或修改启动注册后，需要完整重启进程。
