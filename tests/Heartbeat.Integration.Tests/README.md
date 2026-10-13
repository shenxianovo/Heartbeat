# Heartbeat.Integration.Tests

本项目使用 TUnit 和真实 PostgreSQL 执行集成测试。测试约定见[Heartbeat Testing](../../src/Docs/Heartbeat.Docs/content/docs/testing/index.mdx)。

## 运行

前置条件：Docker 已启动。

从仓库根目录运行：

```sh
dotnet test --project tests/Heartbeat.Integration.Tests/Heartbeat.Integration.Tests.csproj
```

## 生命周期

`PostgresAssemblyHooks` 在程序集的开始钩子中启动 PostgreSQL 容器，在结束钩子中销毁。每个数据库测试用例的开始钩子创建独立数据库并应用项目迁移，结束钩子删除数据库。

用例先释放 API 宿主、上下文和数据源。结束钩子再删除数据库。

## 当前用例

`ModelScenarioTests` 复用 100 个语义场景、4 个允许的对照和 6 个实际采样记录，检查真实 API/PostgreSQL 读回、同 ID 重放、Schema 发现和固定实体响应。`JsonDuplicateApiTests` 检查嵌套和转义后重复字段返回 400 并保留旧内容。

`EntityReferenceTests` 检查统一引用枚举。Observation 返回三个目标标识。没有引用的实体返回空集合。字符串和 JSON 中的标识不自动成为引用。这组用例不创建数据库。

`SavedObserverCanBeReadFromANewContext` 通过 `HeartbeatDbContext` 保存统一索引与 `Observer`，再使用新的上下文读取，核对实体标识、名称和存储表名。

`ObserverApiTests` 通过真实 API 和 PostgreSQL 验证 Observer 保存、统一读取、并发、冲突、校验与事务回滚。每条用例创建自己的 `HeartbeatApiFactory`，只覆盖连接配置。

`EntityApiTests` 验证 Observation 引用、UTC 转换、未知边界、完整替换、固定类别并发、UUIDv7、Unicode 和真实 Kestrel 10 MiB 请求体边界。旧通用 `PUT /entities/{id}` 仍返回 `405`。

`EntitySchemaApiTests` 验证递归声明、默认表列命名、资源登记、幂等、允许显示名称修改、拒绝结构或资源变更、定义键序无关、同 ID 与同资源的并发登记，以及首次建表失败的原子回滚。

`BusinessEntityApiTests` 通过真实 HTTP 和独立 PostgreSQL 检查实际列类型、精确 numeric/bigint、数学整数、字段名安全引用、未知资源、Schema 冲突、完整替换、省略与显式 null、递归对象和数组校验，以及 invalid 内容不留下索引或覆盖已有内容。

### 前台状态 Observer（macOS）

| 测试类 | 检查内容 | 输入或依赖 |
| --- | --- | --- |
| `MacObserverIdentityTests` | 首次创建、重复启动、独立实例、并发首次启动、损坏文件保留 | 临时身份文件 |
| `MacObserverSubmissionTests` | 独立快照、引用、微秒时间、Schema登记、动态提交与统一读回核对 | 采集程序的提交实现、真实 API、独立 PostgreSQL |
| `MacObserverReadBackTests` | 类别或内容不一致时保留 Outbox 记录 | 模拟读取响应 |
| `ForegroundOutboxTests` | 当前时段隔离、重启恢复、事务回滚、事件与休眠边界、断网及响应丢失 | 独立 SQLite、模拟读数和 HTTP |

这些用例不调用 macOS 原生接口。原生读取需在 macOS 上实际运行 [前台状态程序](../../src/Observers/Heartbeat.Observer.ForegroundState/README.md)验证。

前台应用采集的行为 Case 见文档站 [本机身份](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/local-application-identity.mdx)、[时段边界](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/application-period-boundaries.mdx)、[时段保存](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/period-submission.mdx)和[Outbox 恢复](../../src/Docs/Heartbeat.Docs/content/docs/testing/observers/outbox-recovery.mdx)。

### Sources 原生来源流水线

`SourcePipelineTests` 使用实际来源解析器、SQLite Outbox、API 和独立 PostgreSQL：15 份原生合成输入，Git/文件/procfs 三个真实隔离读取，以及精确来源时间与微秒投影对照。每条管道检查持久状态重启、重放身份、逐实体 GET、Schema 发现、后续读回确认及保留用户修改的 Schema 显示名。

标准入口仍使用上面的 PostgreSQL 18.6 Testcontainers。当前云环境的 PostgreSQL 17.11 替代启动结果、聚合 runner 限制与未验证范围见[来源验证](../../src/Docs/Heartbeat.Docs/content/docs/observers/sources/verification.mdx)，不要把替代环境通过写成容器或完整 Aspire 已验证。
