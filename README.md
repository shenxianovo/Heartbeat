# Heartbeat

Heartbeat 忠实保留观测及其必要上下文，为未来提出的问题提供依据。

项目正在重写。重写完成前不部署，也不兼容旧数据、旧客户端或旧接口。

## 启动本地环境

前置条件：

- Docker
- `global.json` 指定的 .NET SDK
- Node.js 24 LTS
- pnpm 12.3.4

首次使用时，在仓库根目录安装 Aspire CLI、还原 EF 工具并信任开发证书：

```sh
dotnet tool install --global Aspire.Cli --version 13.6.3
dotnet tool restore
aspire certs trust
```

启动：

```sh
aspire run
```

Aspire 在本机运行 API、文档站和 Web 控制台，在容器中运行 PostgreSQL 和 NGINX，并在启动 API 前应用数据库迁移。按 Ctrl+C 停止。

| 服务 | 地址 |
| --- | --- |
| API | <http://localhost:8080/api> |
| Web 控制台 | <http://localhost:8081/> |
| 文档站 | <http://localhost:3000/core> |
| Aspire 看板 | <https://localhost:18888> |

后台运行、日志和资源操作见 [AppHost README](tools/Heartbeat.AppHost/README.md)。

## 采集前台应用

在 macOS 上持续采集前台应用，并保存到本地 API：

```sh
dotnet run --project src/Observers/Heartbeat.Observer.ForegroundState
```

参数和文件位置见 [Foreground State README](src/Observers/Heartbeat.Observer.ForegroundState/README.md)。

## 验证

```sh
dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD --quality
```

命令说明见 [Heartbeat.Dev README](tools/Heartbeat.Dev/README.md)。

## 目录

| 目录 | 内容 |
| --- | --- |
| `src/Core` | 实体、契约和模型规则 |
| `src/Backend` | API、应用操作和 PostgreSQL 实现 |
| `src/Observers` | Observer 程序 |
| `src/Frontend/Heartbeat.Web` | Web 控制台 |
| `src/Docs/Heartbeat.Docs` | 文档站，长期文档的唯一位置 |
| `tests` | 模型、集成和开发工具测试 |
| `tools/Heartbeat.AppHost` | Aspire 本地编排 |
| `tools/Heartbeat.Dev` | 验证、质量扫描和证据管理 |

## 文档

从 [Core](src/Docs/Heartbeat.Docs/content/docs/core/index.mdx) 开始读。Agent 的工作规则见 [AGENTS.md](AGENTS.md)。

## 许可

Copyright © 2026 shenxianovo

仓库按内容使用两种许可：

| 内容 | 许可 |
| --- | --- |
| 源码、测试、脚本、构建与运行配置、文档站程序 | [AGPL-3.0-only](LICENSE) |
| OpenAPI 契约，以及由它生成的接口参考 | [AGPL-3.0-only](LICENSE) |
| 文档中的代码、命令和配置示例 | [AGPL-3.0-only](LICENSE) |
| 文档正文和原创图解，包括各级 README、`AGENTS.md` 和文档站内容 | [CC BY-NC-SA 4.0](LICENSE-DOCS) |
| 图解的源文件和数据，例如 Mermaid 图和 `relations.json` | [CC BY-NC-SA 4.0](LICENSE-DOCS) |

表中未列出的文件使用 AGPL-3.0-only。第三方内容保留各自的许可。文档站页面和 Markdown 导出沿用原内容的许可。

代码可以商用。分发软件，或通过网络向用户提供修改版时，须按 AGPL 提供对应源码。文档限非商业使用，这一限制不影响代码的商用，也不限制独立实现文档描述的设计。文档的商业使用请通过 [GitHub](https://github.com/shenxianovo) 联系作者。

权利和义务以许可证全文为准。
