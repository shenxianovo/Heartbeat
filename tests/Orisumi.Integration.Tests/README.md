# Orisumi.Integration.Tests

Orisumi.Integration.Tests 用 TUnit 运行集成测试，覆盖实体 API 与 PostgreSQL 存储、API 遥测、.NET SDK、Runtime，以及前台状态 Observer 的本地行为。测试分工见 [Testing](../../src/Docs/Orisumi.Docs/content/docs/testing/index.mdx)。

## 运行

在仓库根目录运行：

```sh
dotnet test --project tests/Orisumi.Integration.Tests/Orisumi.Integration.Tests.csproj
```

文档站从仓库根目录的 `TestResults/` 读取 `Orisumi.Integration.Tests-*.tunit-report.json`，见 [文档站 README · 测试 Case 与报告](../../src/Docs/Orisumi.Docs/README.md#测试-case-与报告)。

对应的 Case 和测试方法见 [Testing · 集成测试](../../src/Docs/Orisumi.Docs/content/docs/testing/index.mdx#integration)。这些测试不调用 macOS 原生接口，原生读取的验证见 [Foreground State README](../../src/Observers/Orisumi.Observer.ForegroundState/README.md#验证)。
