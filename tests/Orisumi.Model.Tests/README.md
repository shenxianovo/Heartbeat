# Orisumi.Model.Tests

Orisumi.Model.Tests 检查 Core 和 Application 的规则，不需要数据库。测试分工见 [Testing](../../src/Docs/Orisumi.Docs/content/docs/testing/index.mdx)。

## 运行

在仓库根目录运行：

```sh
dotnet test --project tests/Orisumi.Model.Tests/Orisumi.Model.Tests.csproj
```

## 测试类

每个测试用注释写明它验证的规则编号，例如 `// VERSION-7`。

| 测试类 | 检查内容 |
| --- | --- |
| `VersionTests` | 写入方生成的身份、版本链、冲突、重试、引用目标和逐个保存 |
| `SchemaTests` | EntitySchema 的声明校验、类型不变、Schema 版本的使用、resourceName |
| `SchemaBoundaryTests` | 字段值与类型的匹配，`integer` 按数学值判断，`required` 和 `nullable` 分别生效 |
| `JsonAmbiguityTests` | 解码后重复的字段名被拒绝，包括数组中对象的字段；只是大小写不同的字段名和数组中的重复值仍然有效 |
| `ObservationTests` | Observer 与观测内容、输入集、事实时间和时区 |
| `EntityReferenceTests` | 引用的枚举：每个引用保留所在的位置，嵌套的引用全部列出 |
| `DeletionTests` | 按实体删除、身份永久占用、引用保留，以及有数据的 EntitySchema 不能删除 |

`InMemoryEntityStore` 是测试用的事务化内存存储，`ModelFixture` 提供可控的 `TimeProvider` 和常用的版本。
