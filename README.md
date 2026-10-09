# Heartbeat

忠实保留观测及其必要上下文，为未来提出的问题提供依据。

项目正在重写。重写完成前不部署，不兼容旧数据、旧客户端或旧接口。

## 启动

需要 Docker、仓库指定的 .NET SDK、Node.js 和 pnpm。首次安装开发工具并还原 EF 工具：

```sh
dotnet tool install --global Aspire.Cli --version 13.6.1
dotnet tool restore
aspire certs trust
```

从仓库根目录启动本地开发环境：

```sh
aspire run
```

Aspire 在本机运行 API 与文档站，用容器运行 PostgreSQL 和 NGINX，并自动完成数据库迁移。API：<http://localhost:8080/api>；文档站：<http://localhost:3000/core>；看板：<https://localhost:18888>，仅供本机开发使用，无需登录令牌。

后台启动、日志、资源操作和停止见 [AppHost README](tools/Heartbeat.AppHost/README.md)。构建、测试与质量扫描继续使用 [开发工具](tools/Heartbeat.Dev/README.md)。Dockerfile 保留镜像构建能力；本地编排以 AppHost 为准，服务器发布流程尚未建设。

macOS 上读取一次前台应用并提交到本地 API：

```sh
dotnet run --project src/Observers/Heartbeat.Observer.ForegroundState
```

前台状态 Observer 的实例身份、API 地址和运行结果见 [前台状态程序 README](src/Observers/Heartbeat.Observer.ForegroundState/README.md)。能力与平台客户端的分工见 [Observers 概览](src/Docs/Heartbeat.Docs/content/docs/observers/index.mdx)。

## 文档

- [模型与长期文档](src/Docs/Heartbeat.Docs/content/docs/core/index.mdx)
- [后端运行](src/Backend/Heartbeat.Api/README.md)
- [文档站维护](src/Docs/Heartbeat.Docs/README.md)
- [测试运行](tests/Heartbeat.Integration.Tests/README.md)
- [Agent 规则](AGENTS.md)
