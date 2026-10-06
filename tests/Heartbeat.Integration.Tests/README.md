# Heartbeat.Integration.Tests

使用 TUnit 和真实 PostgreSQL 的集成测试。测试约定见[Heartbeat Testing](../../src/Docs/Heartbeat.Docs/content/docs/testing/index.mdx)。

## 运行

需要可用的 Docker 环境。从仓库根目录运行：

```sh
dotnet test --project tests/Heartbeat.Integration.Tests/Heartbeat.Integration.Tests.csproj
```

## 生命周期

`PostgresAssemblyHooks` 在程序集的开始钩子中启动 PostgreSQL 容器，在结束钩子中销毁。每个数据库测试用例的开始钩子创建独立数据库并应用项目迁移，结束钩子删除数据库。

测试体使用的上下文和数据源先释放，再由结束钩子删除数据库。

## 当前用例

`SavedObserverCanBeReadFromANewContext` 通过 `HeartbeatDbContext` 保存统一索引与 `Observer`，再使用新的上下文读取，核对实体标识、名称和存储表名。
