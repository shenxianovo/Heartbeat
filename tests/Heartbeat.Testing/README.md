# Heartbeat.Testing

本项目提供公共数据库测试工具。测试约定见[Heartbeat Testing](../../src/Docs/Heartbeat.Docs/content/docs/testing/index.mdx)。

运行数据库工具前，必须启动 Docker。

## 使用

每次测试运行创建一个实例，每个需要数据库的用例创建一个数据库：

```csharp
using Heartbeat.Testing;

await using var instance = await PostgresTestInstance.StartAsync();
await using var database = await instance.CreateDatabaseAsync();
var connectionString = database.ConnectionString;
```

将 `connectionString` 通过 `ConnectionStrings:Heartbeat` 提供给被测后端。

用例结束时，依次清理资源：

1. 释放被测后端及其连接。
2. 释放 `TestDatabase`。释放时，`TestDatabase` 删除用例数据库。

所有用例结束后，释放 `PostgresTestInstance`。释放时，该实例销毁容器。

两个创建方法都接受可选的 `CancellationToken`。
