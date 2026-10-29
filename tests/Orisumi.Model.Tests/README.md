# Orisumi.Model.Tests

Orisumi.Model.Tests 检查 Core 和 Application 的规则，不需要数据库。测试分工见 [Testing](../../src/Docs/Orisumi.Docs/content/docs/testing/index.mdx)。

## 运行

在仓库根目录运行：

```sh
dotnet test --project tests/Orisumi.Model.Tests/Orisumi.Model.Tests.csproj
```

## 编写

每个测试用注释写明它验证的规则编号，例如 `// VERSION-7`。`InMemoryEntityStore` 是测试用的事务化内存存储，`ModelFixture` 提供可控的 `TimeProvider` 和常用的版本。
