# Heartbeat.Model.Tests

Heartbeat.Model.Tests 检查 Core 和 Application 的规则，不需要数据库。测试分工见 [Testing](../../src/Docs/Heartbeat.Docs/content/docs/testing/index.mdx)。

## 运行

在仓库根目录运行：

```sh
dotnet test --project tests/Heartbeat.Model.Tests/Heartbeat.Model.Tests.csproj
```

## 测试类

| 测试类 | 检查内容 |
| --- | --- |
| `JsonAmbiguityTests` | 解码后重复的字段名被拒绝，包括数组中对象的字段。只是大小写不同的字段名和数组中的重复值仍然有效 |
| `SchemaBoundaryTests` | 整数按数学值判断是否在 Int64 范围内，`required` 和 `nullable` 分别生效 |
| `EntityReferenceTests` | `GetReferences()`：Observation 返回三个引用且不含自身，Observer 和 EntitySchema 不把内容中的标识当作引用 |
