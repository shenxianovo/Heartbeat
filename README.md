# Heartbeat

Heartbeat 将一个人在数字世界中的异构活动痕迹记录为时间有序的观测轨道。

职责分工：**采集 Collector 做，交付 Hub 做，存储后端做，展示前端做。**
Collector 向 Hub 提交逻辑声明与 Record，不持有后端 ID。Hub 持久接管后负责注册、Track 映射与上传；后端保存公共记录结构和任意 JSON value，具体内容由读取和展示模块解释。

当前重写完成前不部署，不保留旧接口、旧数据格式或旧客户端的兼容实现。

## 文档目录

- [领域语言](CONTEXT.md)：Heartbeat 记录领域的核心术语。
- [架构决策](docs/adr)：已经接受的关键设计决策，新增 ADR 使用 [仓库模板](docs/adr/ADR-TEMPLATE.md)。
- [记录存储模型](docs/recording-storage-model.md)：`Timeline -> Collector -> Track -> Record` 四层模型、字段和约束。
- [记录 HTTP 接口](docs/recording-api.md)：Collector 注册、Track 获取、Record 上传和 Track 级重放查询。
- [桌面前台应用协议 v1](docs/protocols/desktop-application-foreground-v1.md)：当前已实现的首个 Record 协议。
- [记录模型持久化说明](src/Backend/Heartbeat.Infrastructure/Persistence/README.md)：EF Core / PostgreSQL 映射约定。
- [macOS Collector](src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md)：最小桌面 Collector 的运行方式。
- [Hub 记录交付](docs/hub-record-delivery.md)：SQLite 持久接管、后台上传、恢复及桌面接入。
- [Next 前端](src/Frontend/Heartbeat.Web/README.md)：本地运行、登录配置、Record value 展示组件扩展与验证。
- [本地开发](docs/development.md)：统一 Docker 启动、热更新、生产镜像验收与首次配置。
- [未决设计](docs/recording-open-questions.md)：未交接数据、断采规则、设备关联等尚未确认的问题。
- [Agent 规则](AGENTS.md)：协作约束和本仓库的工程规则。

## 项目结构

```text
src/
├── Backend/
│   ├── Heartbeat.Api/             # ASP.NET Core 宿主和 HTTP 端点
│   ├── Heartbeat.Application/     # 用例和应用接口
│   ├── Heartbeat.Domain/          # 记录模型和不变量
│   └── Heartbeat.Infrastructure/  # EF Core 和 PostgreSQL 适配器
├── Collectors/
│   └── Heartbeat.Collector.Desktop.Mac/  # macOS 观测和 Record 生成
├── Frontend/
│   └── Heartbeat.Web/                  # Next.js / React 回放与 value 展示组件
└── Hub/
    ├── Heartbeat.Hub.Client/           # 轻量提交结构与 HTTP 客户端
    ├── Heartbeat.Hub/                  # SQLite 接管、后端映射和上传
    └── Heartbeat.Hub.Host/             # HTTP 接收和独立后台上传宿主

tests/
```

解决方案和共享 .NET 构建配置放在仓库根目录，供未来同级的 .NET 项目复用。

## 技术栈

- .NET 10 / ASP.NET Core
- Entity Framework Core 10 with Npgsql
- PostgreSQL 18
- Docker Compose 本地开发环境
- Next.js App Router / React / TypeScript

## 本地运行

默认在 Docker 中启动 Web、API 和 PostgreSQL；数据库迁移会在 API 启动前执行：

```bash
./scripts/dev.sh up
```

访问 <http://localhost:3000>。Web 通过同源 `/api/*` 转发到 API；宿主端口只绑定回环地址。认证服务需允许 `http://localhost:3000/auth/callback` 回调。

显式选择服务会替换默认组合，并只补足必要依赖：

```bash
./scripts/dev.sh up api
./scripts/dev.sh up hub
./scripts/dev.sh up desktop
```

开发模式在容器中运行 `next dev` 和 `dotnet watch`。macOS Desktop Collector 在宿主前台运行，选择 `desktop` 会自动启动 Hub；Hub 不依赖 API 或数据库，可以离线接管记录。

首次启动 Hub 或 Desktop Collector 前完成配置：

```bash
./scripts/setup.sh
```

使用同一服务拓扑构建并运行标准生产镜像：

```bash
./scripts/dev.sh up --release
```

日志、状态、按服务停止、数据重置、端口和环境变量见[本地开发说明](docs/development.md)。普通 `up`/`down` 保留 PostgreSQL 与 Hub SQLite 卷；Initial migration 改变时运行 `./scripts/dev.sh reset` 明确清空本地数据。
