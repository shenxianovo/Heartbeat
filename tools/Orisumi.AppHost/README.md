# Orisumi.AppHost

Orisumi.AppHost 用 Aspire 编排本地开发环境，定义资源、依赖关系、启动顺序和端口。环境分工见 [ADR · 后端运行与本地环境](../../src/Docs/Orisumi.Docs/content/docs/adr/backend-runtime.mdx#local-environment)，启动和隔离的方式见 [ADR · 开发环境的启动与隔离](../../src/Docs/Orisumi.Docs/content/docs/adr/development-environment.mdx)。

## 启动

用 `orisumi env up` 启动，命令和选项见 [Orisumi.Dev README · 环境](../Orisumi.Dev/README.md#环境)。`orisumi` 把要启动的服务、端口和数据卷作为配置传给 AppHost。前置条件和首次使用的准备见[仓库 README · 启动本地环境](../../README.md#启动本地环境)。`aspire.config.json` 指向本项目并启用 watch。

## 资源

| 资源 | 运行方式 | 说明 |
| --- | --- | --- |
| `db` | 容器 | PostgreSQL 18.6，数据保存在所选的数据卷 |
| `migrate` | 本机进程 | `db` 就绪后用 EF 工具应用迁移，完成后退出 |
| `api` | 本机进程 | `migrate` 成功后启动，使用 .NET watch 热更新 |
| `docs` | 本机进程 | 按锁文件安装依赖后运行 `pnpm dev` |
| `web` | 本机进程 | 运行 `npm ci` 后启动 Vite |
| `application-identity` | 本机进程 | `api` 启动后运行[应用识别 Observer](../../src/Observers/Orisumi.Observer.ApplicationIdentity/README.md)，直接连接 API，没有对外端口 |
| `nginx` | 容器 | 提供对外入口，见[入口](#入口) |

`migrate` 失败时，`api` 不启动。只选部分服务时，AppHost 补齐它们的依赖，`nginx` 总是添加。

## 入口

主检出目录使用以下固定端口。工作树的端口由 `orisumi` 分配，用 `orisumi env status` 查看。

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

三个入口总是监听。API 没有启动时，转给 API 的路径返回 `503` 和 `application/problem+json`；文档站或 Web 控制台没有启动时，它的入口对其余路径返回 `503`。请求体上限 10 MiB，超过时返回 `413`。NGINX 配置在 [docker/nginx/](../../docker/nginx/)。

## 环境操作

日常操作用 `orisumi env`，见 [Orisumi.Dev README · 环境](../Orisumi.Dev/README.md#环境)。其他 Aspire 命令，例如 `aspire describe`、`aspire resource api rebuild`，在检出目录中可以直接使用。

停止环境会停止本机进程和容器。默认卷保留，临时卷删除。

## 配置

`appsettings.json` 提供主检出目录的端口、默认卷名和开发用的数据库账号，数据库名、用户名和密码都是 `orisumi`。`orisumi` 用命令行参数覆盖服务、端口和数据卷。覆盖密码时使用 Aspire user secrets。

应用识别 Observer 的配置放在检出目录的 `.local/application-identity.json`，这个文件不进 Git：

```json
{ "apiKey": "<OpenAI API Key>", "model": "gpt-6.1-terra", "intervalSeconds": 60 }
```

AppHost 把 `apiKey` 作为 `OPENAI_API_KEY` 交给它。环境变量 `OPENAI_API_KEY`、`ApplicationIdentity__Model` 和 `ApplicationIdentity__IntervalSeconds` 优先于这个文件。修改后重新启动环境。它的数据目录是 `.artifacts/application-identity/<卷名>/`，每个数据卷一份。

Aspire 只用于本地开发。镜像的构建见 [API README · 镜像](../../src/Backend/Orisumi.Api/README.md#镜像) 和 [文档站 README · 镜像](../../src/Docs/Orisumi.Docs/README.md#镜像)。

## 验证

修改编排、代理或热更新配置后，先运行环境场景：

```sh
orisumi verify scenarios environment
```

它检查入口转发、`413`、部分启动时的 `503`、迁移失败时 API 不启动，以及停止后数据保留，见 [Orisumi.Dev README · 冒烟场景](../Orisumi.Dev/README.md#冒烟场景)。

然后用 `orisumi env up --temp` 启动环境，逐项检查场景之外的行为：

1. 在文档站的 Playground 中发送请求，确认同源代理可用。
2. 打开文档首页和报告页。
3. 修改 API 的一个已有处理方法，确认热更新后 HTTP 响应变化，然后恢复修改。
4. 修改一页文档正文，确认页面显示新内容，然后恢复修改。
5. 打开 Web 控制台，确认 `/api/` 同源代理和 Vite HMR 可用。

检查结束后运行 `orisumi env down`，临时卷随之删除。

### API 遥测验证

```sh
orisumi env up api --temp
curl -i http://localhost:8080/api/entities/019a0d3b-1d20-7000-8000-000000000001
orisumi env telemetry logs api
orisumi env telemetry traces api
```

在工作树中运行时，把地址换成 `orisumi env status` 给出的 API 入口。

1. 确认请求返回 `404`。
2. 确认有结构化日志，HTTP span 下有 Npgsql span。
3. 在看板中选择 `api`，确认有 `http.server.request.duration` 和 `dotnet.*` 指标。指标按导出周期刷新，最多约一分钟。指标只能在看板或它的遥测 API 中读取。

检查结束后运行 `orisumi env down`。

另外，在不设置 `OTEL_EXPORTER_OTLP_ENDPOINT` 的情况下单独运行 API 并发起读取，确认只有控制台日志。自动测试见 `TelemetryTests`。
