# Orisumi.Testing

Orisumi.Testing 为数据库测试提供 PostgreSQL 容器和独立的测试数据库。测试分工见 [Testing](../../src/Docs/Orisumi.Docs/content/docs/testing/index.mdx)。

使用前，先启动 Docker。

## 用法

每次测试运行启动一个实例，每个用到数据库的用例创建一个数据库：

```csharp
using Orisumi.Testing;

await using var instance = await PostgresTestInstance.StartAsync();
await using var database = await instance.CreateDatabaseAsync();
var connectionString = database.ConnectionString;
```

把 `connectionString` 通过 `ConnectionStrings:Orisumi` 交给被测的后端。

两个创建方法都接受可选的 `CancellationToken`。

## 清理顺序

1. 用例结束时，先释放被测后端及其数据库连接。
2. 再释放 `TestDatabase`，`TestDatabase` 会删除这个用例的数据库。
3. 所有用例结束后，释放 `PostgresTestInstance`，`PostgresTestInstance` 会销毁容器。

数据库连接没有释放时，删除数据库会失败。
