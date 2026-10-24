# Heartbeat.Integration.Tests

Heartbeat.Integration.Tests 用 TUnit 运行集成测试，目前覆盖前台状态 Observer 的本地行为，以及 API 的启动和遥测。实体 API 和数据库存储正按版本模型重新设计，相应的测试随它们重建。测试分工见 [Testing](../../src/Docs/Heartbeat.Docs/content/docs/testing/index.mdx)。

## 运行

在仓库根目录运行：

```sh
dotnet test --project tests/Heartbeat.Integration.Tests/Heartbeat.Integration.Tests.csproj
```

文档站从仓库根目录的 `TestResults/` 读取 `Heartbeat.Integration.Tests-*.tunit-report.json`，见 [文档站 README · 测试 Case 与报告](../../src/Docs/Heartbeat.Docs/README.md#测试-case-与报告)。

## 测试类

### API

| 测试类 | 检查内容 |
| --- | --- |
| `TelemetryTests` | 未配置 OTLP 时不注册导出器。配置后检查结构化日志、HTTP 追踪、HTTP 与 .NET 指标，以及三类数据的网络导出 |

### 前台状态 Observer

| 测试类 | 检查内容 | 依赖 |
| --- | --- | --- |
| `MacObserverIdentityTests` | 首次创建、重复启动、独立实例、并发首次启动、损坏文件保留 | 临时身份文件 |
| `ForegroundTimelineTests` | 时段的延长、切换、中断和时间跳变 | 模拟读数 |
| `LocalApplicationIdentityTests` | 本机应用身份的复用和新建、并发登记 | 临时登记文件 |
| `ForegroundOutboxTests` | 进行中时段只在本地、重启恢复、事务回滚、事件和休眠边界 | 独立 SQLite、模拟读数 |

这些测试不调用 macOS 原生接口。原生读取的验证方法见 [Foreground State README](../../src/Observers/Heartbeat.Observer.ForegroundState/README.md#验证)。

对应的 Case：[本机应用身份](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/local-application-identity.mdx)、[时段边界](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/application-period-boundaries.mdx)、[Outbox 恢复](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/outbox-recovery.mdx)。
