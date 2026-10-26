# Orisumi.Integration.Tests

Orisumi.Integration.Tests 用 TUnit 运行集成测试，覆盖实体 API 与 PostgreSQL 存储、API 遥测、.NET SDK 的提交，以及前台状态 Observer 的本地行为。测试分工见 [Testing](../../src/Docs/Orisumi.Docs/content/docs/testing/index.mdx)。

## 运行

在仓库根目录运行：

```sh
dotnet test --project tests/Orisumi.Integration.Tests/Orisumi.Integration.Tests.csproj
```

文档站从仓库根目录的 `TestResults/` 读取 `Orisumi.Integration.Tests-*.tunit-report.json`，见 [文档站 README · 测试 Case 与报告](../../src/Docs/Orisumi.Docs/README.md#测试-case-与报告)。

## 测试类

### API

| 测试类 | 检查内容 |
| --- | --- |
| `EntityApiTests` | 提交、读取、列出与查询、删除和错误格式，以及两个服务实例的首版冲突 |
| `ReferrerApiTests` | 列出引用方：引用位置、按目标实体归集、当前版本与全部版本、分页，以及删除目标或引用方之后的结果 |
| `TelemetryTests` | 未配置 OTLP 时不注册导出器。配置后检查结构化日志、HTTP 追踪、HTTP 与 .NET 指标，以及三类数据的网络导出 |

### Observer 与 SDK

| 测试类 | 检查内容 | 依赖 |
| --- | --- | --- |
| `MacObserverIdentityTests` | 首次创建、重复启动、独立实例、并发首次启动、损坏文件保留 | 临时身份文件 |
| `ForegroundTimelineTests` | 时段的延长、切换、中断和时间跳变 | 模拟读数 |
| `LocalApplicationIdentityTests` | 本机应用身份的复用和新建、并发登记 | 临时登记文件 |
| `ForegroundSubmissionTests` | 进行中时段只在本地、重启恢复、事务回滚、事件和休眠边界 | 独立 SQLite、模拟读数 |
| `ObserverSdkTests` | SDK 的登记、草稿与策略、确认与重试、拒绝处理，以及前台时段的上传和拒绝处理 | 独立 SQLite；部分用例需要独立 PostgreSQL |

这些测试不调用 macOS 原生接口。原生读取的验证方法见 [Foreground State README](../../src/Observers/Orisumi.Observer.ForegroundState/README.md#验证)。

对应的 Case 见 [Testing · 集成测试](../../src/Docs/Orisumi.Docs/content/docs/testing/index.mdx#integration)。
