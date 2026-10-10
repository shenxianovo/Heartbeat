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
| `MacObserverReadBackTests` | 类别或内容不一致时报告核对失败 | 模拟读取响应 |

这些用例不调用 macOS 原生接口。原生读取需在 macOS 上实际运行 [前台状态程序](../../src/Observers/Heartbeat.Observer.ForegroundState/README.md)验证。
