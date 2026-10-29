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

API 进程中同时运行[服务端托管](../../Docs/Orisumi.Docs/content/docs/observers/submission/index.mdx#server)，它托管的应用识别 Observer 的配置见[应用识别 README · 运行](../../Observers/Orisumi.Observer.ApplicationIdentity/README.md#运行)。

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

遥测不读取请求体，不记录数据库参数值。

## 修改接口

1. 修改 [openapi.json](../../Docs/Orisumi.Docs/content/docs/api/openapi.json)。
2. 修改 API 中的实现。
3. 修改集成测试。

后端构建不生成也不覆盖 `openapi.json`。

## 镜像

```sh
docker build -f src/Backend/Orisumi.Api/Dockerfile -t orisumi-api:local .
```

最终镜像以非 root 用户运行，并包含 `/app/efbundle` 供独立的迁移进程使用。

## 验证

模型规则由[模型测试](../../../tests/Orisumi.Model.Tests/README.md)验证，接口、存储和遥测由[集成测试](../../../tests/Orisumi.Integration.Tests/README.md)验证。

修改构建或热更新配置后，在独立的 Aspire 环境中验证：

1. 启动独立环境，见 [AppHost README · 验证](../../../tools/Orisumi.AppHost/README.md#验证)。
2. 检查启动日志中没有 `Failed to read ... staticwebassets.development.json`，原因见 `Orisumi.Api.csproj` 中 `StaticWebAssetsEnabled` 的注释。
3. 修改一个已有处理方法的返回值。
4. 检查日志出现 `C# and Razor changes applied`，并且 HTTP 响应随之变化。
5. 停止独立环境，删除它的验证卷。

新增端点或修改启动注册后，需要完整重启进程。
