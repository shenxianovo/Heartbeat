# Heartbeat.AppHost

Heartbeat.AppHost 用 Aspire 编排本地开发环境。环境分工见 [ADR · 后端运行与本地环境](../../src/Docs/Heartbeat.Docs/content/docs/adr/backend-runtime.mdx#local-environment)。

## 启动

前置条件：

- Docker
- `global.json` 指定的 .NET SDK
- Node.js 24 LTS
- pnpm 12.3.4

首次使用时，在仓库根目录运行：

```sh
dotnet tool install --global Aspire.Cli --version 13.6.3
dotnet tool restore
aspire certs trust
```

`aspire certs trust` 会请求信任本机的 .NET 开发证书，按系统提示确认。

启动环境：

```sh
aspire run
```

`aspire run` 在前台运行，按 Ctrl+C 停止。`aspire.config.json` 指向本项目并启用 watch。

## 资源

| 资源 | 运行方式 | 说明 |
| --- | --- | --- |
| `db` | 容器 | PostgreSQL 18.6，数据保存在命名卷 `heartbeat-aspire-postgres-data` |
| `migrate` | 本机进程 | `db` 就绪后用 EF 工具应用迁移，完成后退出 |
| `api` | 本机进程 | `migrate` 成功后启动，使用 .NET watch 热更新 |
| `docs` | 本机进程 | 按锁文件安装依赖后运行 `pnpm dev` |
| `web` | 本机进程 | 运行 `npm ci` 后启动 Vite |
| `nginx` | 容器 | 提供对外入口，见[入口](#入口) |

`migrate` 失败时，`api` 不启动。

## 入口

| 入口 | 地址 |
| --- | --- |
| API | <http://localhost:8080/api> |
| Web 控制台 | <http://localhost:8081/> |
| 文档站 | <http://localhost:3000/core> |
| Aspire 看板 | <https://localhost:18888> |
| PostgreSQL | `localhost:54329` |

NGINX 的转发规则：

| 入口 | 转发 |
| --- | --- |
| 8080 | `/api/…` 去掉前缀后转给 API，其他路径返回 `404` |
| 3000 | `/api/entities/…` 转给 API，其他路径转给文档站 |
| 8081 | `/api/…` 去掉前缀后转给 API，其他路径转给 Vite，支持 HMR WebSocket |

- 模板是 [docker/nginx/default.conf](../../docker/nginx/default.conf)。AppHost 把 API、文档站和 Web 控制台的内部地址注入为 `API_HOST`、`API_PORT`、`DOCS_HOST`、`DOCS_PORT`、`WEB_HOST`、`WEB_PORT`。
- NGINX 通过 Docker DNS 解析上游，缓存 30 秒。每个上游保留最多 16 条空闲连接。
- 连接超时 5 秒，读写超时 60 秒。请求体上限 10 MiB，超过时返回 `413` 和 `application/problem+json`。

看板使用 HTTPS，只监听本机，允许匿名访问。看板、OTLP 接收和资源服务分别使用 18888、18889、18891 端口。端口被其他环境或独立的文档站进程占用时，先停止那个进程。

## 环境操作

在仓库根目录运行：

```sh
aspire start                       # 后台启动
aspire describe                    # 查看资源状态
aspire wait nginx --timeout 180    # 等待 API 和文档站都能通过代理访问
aspire logs api --follow
aspire resource api restart
aspire resource api rebuild
aspire stop
```

停止环境会停止本机进程和容器，保留数据库命名卷。

## 配置

`appsettings.json` 提供端口、数据库卷名和开发用的数据库账号。数据库名、用户名和密码都是 `heartbeat`。可以用命令行参数覆盖配置，例如 `--Ports:Api=18080`。覆盖密码时使用 Aspire user secrets。

文档站从仓库根目录的 `TestResults` 读取测试报告。

## 镜像

```sh
docker build -f src/Backend/Heartbeat.Api/Dockerfile -t heartbeat-api:local .
docker build -f src/Docs/Heartbeat.Docs/Dockerfile -t heartbeat-docs:local .
```

Aspire 只用于本地开发。部署配置和生产监控尚未建设。

## 验证

修改编排、代理或热更新配置后，在独立的端口和数据库卷上验证。运行前先停止其他 AppHost，因为验证环境与它们共用看板配置。需要并行时，使用 `--isolated` 并核对实际分配的端口和卷。

```sh
aspire run --detach -- --Ports:Api=18080 --Ports:Docs=13000 --Ports:Web=18081 --Ports:Database=54339 --DatabaseVolume=heartbeat-aspire-verification
aspire wait nginx --timeout 180
```

逐项检查：

1. 通过 API 入口和文档入口读取实体。在文档站的 Playground 中发送请求，确认同源代理可用。
2. 提交超过 10 MiB 的请求，确认返回 `413`。
3. 打开文档首页和报告页。
4. 修改 API 的一个已有处理方法，确认热更新后 HTTP 响应变化，然后恢复修改。
5. 修改一页文档正文，确认页面显示新内容，然后恢复修改。
6. 打开 Web 控制台，确认 `/api/` 同源代理和 Vite HMR 可用。
7. 让迁移失败，确认 API 不启动。
8. 运行 `aspire stop`，再用相同参数启动，确认之前保存的实体仍然存在。

验证结束后，停止环境。确认卷属于本次验证后，删除它：

```sh
aspire stop
docker volume rm heartbeat-aspire-verification
```

### API 遥测验证

在隔离环境中验证，避免影响其他环境的会话和开发数据库。隔离模式的资源服务使用 `127.0.0.1`，所以命令额外允许这个主机名：

```sh
aspire run --isolated --detach -- --Ports:Api=18080 --Ports:Docs=13000 --Ports:Web=18081 --Ports:Database=54339 --DatabaseVolume=heartbeat-otel-verification --AllowedHosts='localhost;127.0.0.1'
aspire wait nginx --timeout 180
curl -i http://localhost:18080/api/entities/019a0d3b-1d20-7000-8000-000000000001
aspire otel logs api
aspire otel traces api
```

1. 确认请求返回 `404`。
2. 在环境输出的看板地址中选择 `api`，确认有结构化日志，HTTP span 下有 Npgsql span。
3. 确认有 `http.server.request.duration` 和 `dotnet.*` 指标。指标按导出周期刷新，最多约一分钟。指标只能在看板或它的遥测 API 中读取。

验证结束后，停止环境，删除 `heartbeat-otel-verification` 卷。

另外，在不设置 `OTEL_EXPORTER_OTLP_ENDPOINT` 的情况下单独运行 API 并发起读取，确认只有控制台日志。自动测试见 `TelemetryTests`。
