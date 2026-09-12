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

## Collector 接入进度

已认证 Owner 的 Timeline 需要预先存在。当前已支持依次注册 Collector 和获取 Track：

1. `POST /api/v1/collectors`，提交 `key`、`target` 和 `displayName`，获得稳定 Collector ID。
2. `POST /api/v1/collectors/{collectorId}/tracks`，提交协议名称和版本，获得稳定 Track ID：

```json
{"type":"desktop.application.foreground","version":1}
```

时间模式由服务端确定；重复获取复用同一 Track。[桌面前台应用 v1](docs/protocols/desktop-application-foreground-v1.md) 使用明确的已确认区间，载荷只包含设备标识与平台原生应用标识。

持续状态的内部原子存储入口已实现。批量 Record 上传、重放查询和桌面 Collector 尚未接入。

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
