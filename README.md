# Heartbeat

忠实保留观测及其必要上下文，为未来提出的问题提供依据。

项目正在重写。重写完成前不部署，不兼容旧数据、旧客户端或旧接口。

## 启动

需要 Docker 和 Docker Compose。从仓库根目录运行：

```sh
docker compose up --build -d
```

开发模式：

```sh
docker compose -f compose.yaml -f compose.dev.yaml up --build -d
```

文档站：<http://localhost:3000/core>。API：<http://localhost:8080/api>。容器操作见[后端 README](src/Backend/Heartbeat.Api/README.md#容器操作)。

## 文档

- [模型与长期文档](src/Docs/Heartbeat.Docs/content/docs/core/index.mdx)
- [后端运行](src/Backend/Heartbeat.Api/README.md)
- [文档站维护](src/Docs/Heartbeat.Docs/README.md)
- [测试运行](tests/Heartbeat.Integration.Tests/README.md)
- [Agent 规则](AGENTS.md)
