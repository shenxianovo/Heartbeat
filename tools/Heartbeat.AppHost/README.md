# Heartbeat AppHost

Aspire 是本地开发环境的唯一编排入口。API 与文档站在本机运行，PostgreSQL 与 NGINX 使用容器。职责见 [ADR 0008](../../src/Docs/Heartbeat.Docs/content/docs/adr/0008-aspire-local-development.mdx)。

## 启动

需要 Docker、`global.json` 指定的 .NET SDK、Node.js 24 LTS 或项目支持的更新版本，以及 pnpm 12.3.4。首次使用时，从仓库根目录依次安装 Aspire CLI、还原 EF 工具、信任开发证书并启动环境：

```sh
dotnet tool install --global Aspire.Cli --version 13.6.1
dotnet tool restore
aspire certs trust
aspire run
```

`aspire.config.json` 指向本项目，并启用 watch。API 使用 Aspire 管理的 .NET watch；文档站通过 pnpm 集成安装锁定依赖后执行 `pnpm dev`，保留 Next.js 热更新。

| 入口 | 地址 |
| --- | --- |
| API | <http://localhost:8080/api> |
| 文档站 | <http://localhost:3000/core> |
| 看板 | <https://localhost:18888> |
| PostgreSQL | `localhost:54329` |

首次运行 `aspire certs trust` 时，按系统提示信任本机 .NET 开发证书。看板启用本地匿名访问。打开 `https://localhost:18888` 无需登录令牌。看板使用 HTTPS，仅监听本机并接受 `localhost` 主机名。看板显示资源状态、实际 HTTP 就绪检查和控制台日志。要查看完整调用链、结构化遥测和指标，需要为应用配置 OpenTelemetry。应用尚未配置 OpenTelemetry。

数据库使用 PostgreSQL 18.6，初始化统计扩展并保留诊断配置。数据库就绪后，`migrate` 使用本地 EF 工具应用项目迁移并退出。迁移成功后，AppHost 才启动 API。API 进程不自动建表。

NGINX 使用 AppHost 注入的内部地址访问本机 API 与文档站，保留既有路径、Playground 同源请求和 10 MiB 请求上限。API、文档站的内部端口由 Aspire 分配。看板、遥测和管理接口分别使用 18888、18889、18891；业务入口使用 8080、3000。如果旧环境或独立文档进程占用上述端口，先停止占用端口的进程。

## 环境操作

从仓库根目录运行：

```sh
aspire start
aspire describe
aspire logs api --follow
aspire resource api restart
aspire resource api rebuild
aspire wait nginx --timeout 180
aspire stop
```

`aspire run` 在前台运行，按 Ctrl+C 停止。`aspire start` 在后台运行。

启动 AppHost 后，运行 `aspire wait nginx` 验证资源就绪。该命令等待 API 查询与文档首页均能通过代理访问。看板也提供资源启动、停止、重启和项目重新构建。

停止 AppHost 会停止其本机进程和会话容器，保留 `heartbeat-aspire-postgres-data` 命名卷。再次启动复用该卷。文档依赖和缓存位于本机 `node_modules` 与 `.next`；文档站从仓库 `TestResults` 读取测试报告。

当前前端目录不具备可运行实现，因此尚未纳入本地编排。前台状态 Observer 继续按其 [README](../../src/Observers/Heartbeat.Observer.ForegroundState/README.md) 单独运行。

## 配置与镜像

`appsettings.json` 提供开发参数、公开端口和数据库卷名；可用 .NET 配置的命令行参数或环境变量覆盖。开发用户名和密码均为 `heartbeat`。如需覆盖密码，使用 Aspire user secrets。

Dockerfile 保留 API 与文档站镜像构建能力：

```sh
docker build -f src/Backend/Heartbeat.Api/Dockerfile -t heartbeat-api:local .
docker build -f src/Docs/Heartbeat.Docs/Dockerfile -t heartbeat-docs:local .
```

API 镜像保留 `/app/efbundle`，供独立迁移进程使用。代理模板和数据库初始化脚本也继续保留。服务器发布流程尚未建设；将来可增加 Aspire 的 Compose 发布集成，从同一 AppHost 生成部署配置。本地 `env`、`--release` 和手写 Compose 已移除。

## 验证

运行前，停止其他 AppHost，因为以下验证共用看板配置。如果需要并行运行环境，使用 Aspire 的 `--isolated` 模式，并核对实际分配的端口和卷。

从仓库根目录使用独立端口和数据库卷启动验证环境：

```sh
aspire run --detach -- --Ports:Api=18080 --Ports:Docs=13000 --Ports:Database=54339 --DatabaseVolume=heartbeat-aspire-verification
aspire wait nginx --timeout 180
```

验证以下行为：

1. 通过 API 和文档入口读取实体，检查 Playground 同源代理。
2. 提交超限请求，检查错误响应。
3. 打开文档首页和报告页。
4. 修改 API 已有处理方法。
5. 验证：热更新后 HTTP 响应变化。
6. 恢复 API 修改。
7. 修改文档正文。
8. 验证：页面显示修改后的正文。
9. 恢复文档修改。
10. 停止并重新启动资源，检查数据库持久性。
11. 检查迁移失败时 API 不启动。
12. 检查代理恢复后可以访问 API 和文档站。

```sh
aspire stop
docker volume inspect heartbeat-aspire-verification
```

再次使用相同参数启动环境。读取验证实体，确认停止后数据保留。

验证结束后，停止验证环境。下面的命令会删除验证卷及其数据。确认卷属于本次验证后，运行：

```sh
docker volume rm heartbeat-aspire-verification
```

构建、测试、质量检查与最终验证使用 [Heartbeat.Dev](../Heartbeat.Dev/README.md)。运行检查只证明本地开发行为。
