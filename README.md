# Orisumi

A lasting record of your experiences.

Orisumi 保存一个人经历了什么：来自不同设备和软件的观测及其必要上下文，为未来提出的问题提供依据。

记录以用户为主体。设备、应用和 Agent 会更替，每种数据的结构和含义随数据一起保存，换掉采集和使用它的软件后仍能读懂。Agent 是这些数据的读取方之一，任务执行和外部资源管理由其他组件负责。定位与边界见 [ADR · 定位与边界](src/Docs/Orisumi.Docs/content/docs/adr/positioning.mdx)。

项目正在重写。文档站发布在 <https://docs.orisumi.dev>，后端和客户端在重写完成后部署。重写期间，接口和数据格式会直接变化。

## 启动本地环境

前置条件：

- Docker
- `global.json` 指定的 .NET SDK
- Node.js 24 LTS
- pnpm 12.3.4

首次使用时，在仓库根目录安装 Aspire CLI、还原 EF 工具、信任开发证书，再安装开发工具 `orisumi`。`aspire certs trust` 会请求信任本机的 .NET 开发证书，按系统提示确认。

```sh
dotnet tool install --global Aspire.Cli --version 13.6.3
dotnet tool restore
aspire certs trust
dotnet run --project tools/Orisumi.Dev -- install
```

启动和停止：

```sh
orisumi env up
orisumi env down
```

`orisumi env up` 在后台启动环境：API、文档站和 Web 控制台在本机运行，PostgreSQL 和 NGINX 在容器中运行，API 启动前先应用数据库迁移。

| 服务 | 地址 |
| --- | --- |
| API | <http://localhost:8080/api> |
| Web 控制台 | <http://localhost:8081/> |
| 文档站 | <http://localhost:3000/> |
| Aspire 看板 | <https://localhost:18888> |

只启动部分服务、使用临时数据卷、在工作树中运行另一套环境，以及读取日志和遥测，见 [Orisumi.Dev README · 环境](tools/Orisumi.Dev/README.md#环境)。资源和入口见 [AppHost README](tools/Orisumi.AppHost/README.md)。

## 采集前台应用

在 macOS 上持续采集前台应用，并保存到本地 API：

```sh
dotnet run --project src/Observers/Orisumi.Observer.ForegroundState
```

参数和文件位置见 [Foreground State README](src/Observers/Orisumi.Observer.ForegroundState/README.md)。

## 验证

```sh
orisumi verify changed --base HEAD --quality
```

命令说明见 [Orisumi.Dev README](tools/Orisumi.Dev/README.md)。

## 目录

| 目录 | 内容 |
| --- | --- |
| `src/Core` | 实体、契约和模型规则 |
| `src/Backend` | API、应用操作和 PostgreSQL 实现 |
| `src/Observers` | Observer 程序 |
| `src/Clients/Orisumi.Web` | Web 控制台 |
| `src/Clients/Orisumi.Mac` | macOS 客户端，托管桌面 Observer |
| `src/Docs/Orisumi.Docs` | 文档站，长期文档的唯一位置 |
| `tests` | 模型、集成和开发工具测试 |
| `tools/Orisumi.AppHost` | Aspire 本地编排 |
| `tools/Orisumi.Dev` | 开发工具 `orisumi`：环境、验证、质量扫描和证据管理 |

## 文档

从 [Introduction](src/Docs/Orisumi.Docs/content/docs/start/index.mdx) 开始读，它介绍项目、给出文档地图和阅读路径。[Walkthrough](src/Docs/Orisumi.Docs/content/docs/start/walkthrough.mdx) 用一天的数据串起 Core 的概念。Agent 的工作规则见 [AGENTS.md](AGENTS.md)。

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
