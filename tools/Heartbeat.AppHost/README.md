# Heartbeat.AppHost

Heartbeat.AppHost 用 Aspire 编排本地开发环境，定义资源、依赖关系、启动顺序和端口。环境分工见 [ADR · 后端运行与本地环境](../../src/Docs/Heartbeat.Docs/content/docs/adr/backend-runtime.mdx#local-environment)，启动和隔离的方式见 [ADR · 开发环境的启动与隔离](../../src/Docs/Heartbeat.Docs/content/docs/adr/development-environment.mdx)。

## 启动

用 `hb env up` 启动，命令和选项见 [Heartbeat.Dev README · 环境](../Heartbeat.Dev/README.md#环境)。`hb` 把要启动的服务、端口和数据卷作为配置传给 AppHost。前置条件和首次使用的准备见[仓库 README · 启动本地环境](../../README.md#启动本地环境)。`aspire.config.json` 指向本项目并启用 watch。

## 资源

| 资源 | 运行方式 | 说明 |
| --- | --- | --- |
| `db` | 容器 | PostgreSQL 18.6，数据保存在所选的数据卷 |
| `migrate` | 本机进程 | `db` 就绪后用 EF 工具应用迁移，完成后退出 |
| `api` | 本机进程 | `migrate` 成功后启动，使用 .NET watch 热更新 |
| `docs` | 本机进程 | 按锁文件安装依赖后运行 `pnpm dev` |
| `web` | 本机进程 | 运行 `npm ci` 后启动 Vite |
| `nginx` | 容器 | 提供对外入口，见[入口](#入口) |

- `migrate` 失败时，`api` 不启动。
- AppHost 只添加所选服务和它们的依赖：`api` 带上 `db` 和 `migrate`，`web` 带上 `api`。`nginx` 总是添加。

## 入口

主检出目录使用以下固定端口。工作树的端口由 `hb` 分配，用 `hb env status` 查看。

| 入口 | 地址 |
| --- | --- |
| API | <http://localhost:8080/api> |
| Web 控制台 | <http://localhost:8081/> |
| 文档站 | <http://localhost:3000/> |
| Aspire 看板 | <https://localhost:18888> |
| PostgreSQL | `localhost:54329` |

NGINX 的转发规则：

| 入口 | 转发 |
| --- | --- |
| 8080 | `/api/…` 去掉前缀后转给 API，其他路径返回 `404` |
| 3000 | `/api/versions`、`/api/entities`、`/api/observations` 及其子路径转给 API，其他路径转给文档站 |
| 8081 | `/api/…` 去掉前缀后转给 API，其他路径转给 Vite，支持 HMR WebSocket |

- 三个入口总是监听。API 没有启动时，转给 API 的路径返回 `503` 和 `application/problem+json`。文档站或 Web 控制台没有启动时，它的入口对其余路径返回 `503`。
- 配置在 [docker/nginx/](../../docker/nginx/)：`default.conf` 定义三个入口，`common.conf` 定义公共代理设置、请求体上限和返回 `503` 的内部服务，每个服务的上游在 `<服务>-enabled.conf` 和 `<服务>-disabled.conf` 中。AppHost 总是挂载前两个文件，再为每个服务挂载其中一个上游片段，所以每个入口的端口不随所选服务改变。
- AppHost 把已启动服务的内部地址注入为 `API_HOST`、`API_PORT`、`DOCS_HOST`、`DOCS_PORT`、`WEB_HOST`、`WEB_PORT`。
- NGINX 通过 Docker DNS 解析上游，缓存 30 秒。每个上游保留最多 16 条空闲连接。
- 连接超时 5 秒，读写超时 60 秒。请求体上限 10 MiB，超过时返回 `413` 和 `application/problem+json`。

看板使用 HTTPS，只监听本机，允许匿名访问。主检出目录的看板、OTLP 接收和资源服务分别使用 18888、18889、18891 端口，工作树的环境使用 Aspire 隔离模式分配的端口。端口被其他进程占用时，先停止那个进程。

## 环境操作

日常操作用 `hb env`，见 [Heartbeat.Dev README · 环境](../Heartbeat.Dev/README.md#环境)。其他 Aspire 命令，例如 `aspire describe`、`aspire resource api rebuild`，在检出目录中可以直接使用。

停止环境会停止本机进程和容器。默认卷保留，临时卷删除。

## 配置

`appsettings.json` 提供主检出目录的端口、默认卷名和开发用的数据库账号。数据库名、用户名和密码都是 `heartbeat`。`hb` 用命令行参数覆盖配置：

| 配置 | 含义 |
| --- | --- |
| `Services` | 要启动的服务，逗号分隔，例如 `api,docs`。省略时启动全部服务 |
| `Ports:Api`、`Ports:Docs`、`Ports:Web`、`Ports:Database` | 入口和数据库的端口 |
| `DatabaseVolume` | 数据卷名 |
| `Testing:FailMigration` | 为 `true` 时，`migrate` 更新到不存在的迁移并失败。环境场景用它检查迁移失败时 API 不启动 |

覆盖密码时使用 Aspire user secrets。

文档站从仓库根目录的 `TestResults` 读取测试报告。

## 镜像

```sh
docker build -f src/Backend/Heartbeat.Api/Dockerfile -t heartbeat-api:local .
docker build -f src/Docs/Heartbeat.Docs/Dockerfile -t heartbeat-docs:local .
```

Aspire 只用于本地开发。部署配置和生产监控尚未建设。

## 验证

修改编排、代理或热更新配置后，先运行环境场景：

```sh
hb verify scenarios environment
```

它检查入口转发、`413`、部分启动时的 `503`、迁移失败时 API 不启动，以及停止后数据保留，见 [Heartbeat.Dev README · 冒烟场景](../Heartbeat.Dev/README.md#冒烟场景)。

然后用 `hb env up --temp` 启动环境，逐项检查场景之外的行为：

1. 在文档站的 Playground 中发送请求，确认同源代理可用。
2. 打开文档首页和报告页。
3. 修改 API 的一个已有处理方法，确认热更新后 HTTP 响应变化，然后恢复修改。
4. 修改一页文档正文，确认页面显示新内容，然后恢复修改。
5. 打开 Web 控制台，确认 `/api/` 同源代理和 Vite HMR 可用。

检查结束后运行 `hb env down`，临时卷随之删除。

### API 遥测验证

```sh
hb env up api --temp
curl -i http://localhost:8080/api/entities/019a0d3b-1d20-7000-8000-000000000001
hb env otel logs api
hb env otel traces api
```

在工作树中运行时，把地址换成 `hb env status` 给出的 API 入口。

1. 确认请求返回 `404`。
2. 确认有结构化日志，HTTP span 下有 Npgsql span。
3. 在看板中选择 `api`，确认有 `http.server.request.duration` 和 `dotnet.*` 指标。指标按导出周期刷新，最多约一分钟。指标只能在看板或它的遥测 API 中读取。

检查结束后运行 `hb env down`。

另外，在不设置 `OTEL_EXPORTER_OTLP_ENDPOINT` 的情况下单独运行 API 并发起读取，确认只有控制台日志。自动测试见 `TelemetryTests`。
