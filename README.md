# Heartbeat

Heartbeat 将一个人在数字世界中的异构活动痕迹记录为时间有序的观测轨道。

## 文档目录

- [领域语言](CONTEXT.md)：Heartbeat 记录领域的核心术语。
- [架构决策](docs/adr)：已经接受的关键设计决策，新增 ADR 使用 [仓库模板](docs/adr/ADR-TEMPLATE.md)。
- [记录存储模型](docs/recording-storage-model.md)：`Timeline -> Collector -> Track -> Record` 四层模型、字段和约束。
- [记录 HTTP 接口](docs/recording-api.md)：Collector 注册、Track 获取、Record 上传和 Track 级重放查询。
- [桌面前台应用协议 v1](docs/protocols/desktop-application-foreground-v1.md)：当前已实现的首个 Record 协议。
- [记录模型持久化说明](src/Backend/Heartbeat.Infrastructure/Persistence/README.md)：EF Core / PostgreSQL 映射约定。
- [macOS Collector](src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md)：最小桌面 Collector 的运行方式。
- [未决设计](docs/recording-open-questions.md)：持久队列、断采规则、设备关联等尚未确认的问题。
- [Agent 规则](AGENTS.md)：协作约束和本仓库的工程规则。

## 项目结构

```text
src/
├── Backend/
│   ├── Heartbeat.Api/             # ASP.NET Core 宿主和 HTTP 端点
│   ├── Heartbeat.Application/     # 用例和应用接口
│   ├── Heartbeat.Domain/          # 记录模型和不变量
│   └── Heartbeat.Infrastructure/  # EF Core 和 PostgreSQL 适配器
└── Collectors/
    └── Heartbeat.Collector.Desktop.Mac/  # 独立采样和上传的 macOS Collector

tests/
```

解决方案和共享 .NET 构建配置放在仓库根目录，供未来同级的 .NET 项目复用。

## 技术栈

- .NET 10 / ASP.NET Core
- Entity Framework Core 10 with Npgsql
- PostgreSQL 18
- Docker Compose 本地开发环境

## 本地运行

启动完整的本地服务：

```bash
./scripts/dev.sh
```

脚本还支持 `down`、`logs` 和 `status` 命令。

后端开发时也可以分别启动 PostgreSQL 和 API：

```bash
docker compose up -d db
dotnet tool restore
dotnet restore
dotnet ef database update \
  --project src/Backend/Heartbeat.Infrastructure \
  --startup-project src/Backend/Heartbeat.Api
dotnet run --project src/Backend/Heartbeat.Api
```

API 监听 ASP.NET Core 输出的地址。数据库就绪状态可通过 `/health/ready` 查询。

容器中的 API 地址为 <http://localhost:8080>。
