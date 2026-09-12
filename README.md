# Heartbeat

Heartbeat 将一个人在数字世界中的异构活动痕迹记录为时间有序的观测轨道。

## 技术栈

- .NET 10 / ASP.NET Core
- Entity Framework Core 10 with Npgsql
- PostgreSQL 18
- Docker Compose 本地开发环境

## 目录结构

```text
src/
└── Backend/
    ├── Heartbeat.Api/             # ASP.NET Core 宿主和 HTTP 端点
    ├── Heartbeat.Application/     # 用例和应用接口
    ├── Heartbeat.Domain/          # 记录模型和不变量
    └── Heartbeat.Infrastructure/  # EF Core 和 PostgreSQL 适配器

tests/
```

解决方案和共享 .NET 构建配置放在仓库根目录，供未来同级的 .NET 项目复用。

持久化结构和约定见[记录模型持久化说明](src/Backend/Heartbeat.Infrastructure/Persistence/README.md)。

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
