# Heartbeat.Integration.Tests

Heartbeat.Integration.Tests 用 TUnit、真实的 API 宿主和 PostgreSQL 运行集成测试。测试分工见 [Testing](../../src/Docs/Heartbeat.Docs/content/docs/testing/index.mdx)。

## 运行

前置条件：Docker 正在运行。

在仓库根目录运行：

```sh
dotnet test --project tests/Heartbeat.Integration.Tests/Heartbeat.Integration.Tests.csproj
```

文档站从仓库根目录的 `TestResults/` 读取 `Heartbeat.Integration.Tests-*.tunit-report.json`，见 [文档站 README · 测试 Case 与报告](../../src/Docs/Heartbeat.Docs/README.md#测试-case-与报告)。

## 数据库生命周期

- `PostgresAssemblyHooks` 在所有用例开始前启动一个 PostgreSQL 容器，全部结束后销毁容器。
- 需要数据库的测试类继承 `DatabaseTest`：每个用例创建自己的数据库并应用迁移，结束时删除这个数据库。
- 删除数据库前，用例先释放 API 宿主、`DbContext` 和数据源。

数据库工具见 [Heartbeat.Testing](../Heartbeat.Testing/README.md)。

## 测试类

### 实体 API 与存储

| 测试类 | 检查内容 |
| --- | --- |
| `EntityPersistenceTests` | Observation 直接通过 `DbContext` 写入时，亚微秒部分向下截断 |
| `ObserverApiTests` | Observer 的保存、读取、并发、冲突、校验和事务回滚 |
| `EntityApiTests` | Observation 的引用、UTC 转换、未知边界、完整替换、并发、UUIDv7、Unicode、10 MiB 请求体上限，以及重复字段名（包括嵌套和转义后相同的名称）返回 `400` 且原内容不变 |
| `EntitySchemaApiTests` | 字段声明的递归校验、表名和列名、资源登记、幂等、显示名称修改、拒绝结构或资源变更、并发登记、建表失败时整体回滚 |
| `BusinessEntityApiTests` | 业务表的列类型、`numeric` 和 `bigint` 精度、未登记资源、Schema 冲突、完整替换、省略与 `null`、嵌套对象和数组校验、失败时不留下数据 |
| `TelemetryTests` | 未配置 OTLP 时不注册导出器。配置后检查结构化日志、HTTP 与 Npgsql 的 span 父子关系、HTTP 与 .NET 指标，以及三类数据的网络导出 |

### 前台状态 Observer

| 测试类 | 检查内容 | 依赖 |
| --- | --- | --- |
| `MacObserverIdentityTests` | 首次创建、重复启动、独立实例、并发首次启动、损坏文件保留 | 临时身份文件 |
| `ForegroundTimelineTests` | 时段的延长、切换、中断和时间跳变 | 模拟读数 |
| `ForegroundOutboxTests` | 进行中时段只在本地、重启恢复、事务回滚、事件和休眠边界、断网、响应丢失 | 独立 SQLite、模拟读数和 HTTP |
| `MacObserverSubmissionTests` | 引用、微秒时间、Schema 登记、提交和读回 | 真实 API 和 PostgreSQL |
| `MacObserverReadBackTests` | 类别或内容不一致时保留 Outbox 记录 | 模拟 HTTP 响应 |

这些测试不调用 macOS 原生接口。原生读取的验证方法见 [Foreground State README](../../src/Observers/Heartbeat.Observer.ForegroundState/README.md#验证)。

对应的 Case：[本机应用身份](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/local-application-identity.mdx)、[时段边界](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/application-period-boundaries.mdx)、[时段保存](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/period-submission.mdx)、[Outbox 恢复](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/outbox-recovery.mdx)。
