# Heartbeat.Integration.Tests

使用 TUnit 和真实 PostgreSQL 的集成测试。测试约定见[Heartbeat Testing](../../src/Docs/Heartbeat.Docs/content/docs/testing/index.mdx)。

## 运行

需要可用的 Docker 环境。从仓库根目录运行：

```sh
dotnet test --project tests/Heartbeat.Integration.Tests/Heartbeat.Integration.Tests.csproj
```

## 生命周期

`PostgresAssemblyHooks` 在程序集的开始钩子中启动 PostgreSQL 容器，在结束钩子中销毁。每个数据库测试用例的开始钩子创建独立数据库并应用项目迁移，结束钩子删除数据库。

测试体使用的 API 宿主、上下文和数据源先释放，再由结束钩子删除数据库。

## 当前用例

`SavedObserverCanBeReadFromANewContext` 通过 `HeartbeatDbContext` 保存统一索引与 `Observer`，再使用新的上下文读取，核对实体标识、名称和存储表名。

`ObserverApiTests` 通过真实 API 和 PostgreSQL 验证 Observer 保存、统一读取、并发、冲突、校验与事务回滚。每条用例创建自己的 `HeartbeatApiFactory`，只覆盖连接配置。

`EntityApiTests` 验证通用实体和 Observation 的保存与读取，包括 JSONB 表示限制、未入库的引用、UTC 转换、未知边界、替换内容和不同类别的并发竞争。专门的读取用例通过数据库准备已有内容，所有 HTTP 用例均通过响应断言结果。

Unicode 用例覆盖四类保存入口的非法字段名与字符串值，确认返回 `400` 且不留下实体。请求体边界用例通过真实 Kestrel 监听随机本地端口，验证恰好 10 MiB 可以保存、超限请求返回 JSON `413`，并覆盖分块传输。

`ObservationSchemaApiTests` 验证观测定义的创建、重复提交、同 ID 内容更新、JSON 值类型、仅含名称和定义的读回内容、非法替换和类别冲突。是否需要新 ID 由提交方判断，后端不比较 JSON 定义的语义。

`OpenApiContractTests` 检查后端构建生成的规范：专用请求类型、必填可空时间、路径身份、无正文的成功响应、多类别读取和统一错误格式。该组用例不创建数据库。

### 前台状态 Observer（macOS）

| 测试类 | 检查内容 | 输入或依赖 |
| --- | --- | --- |
| `MacObserverIdentityTests` | 首次创建、重复启动、独立实例、并发首次启动、损坏文件保留 | 临时身份文件 |
| `MacObserverSubmissionTests` | 快照、引用、微秒时间、重复提交、观测写入失败时保留已保存实体 | 采集程序的提交实现、真实 API、独立 PostgreSQL |
| `MacObserverReadBackTests` | 类别或内容不一致时报告核对失败 | 模拟读取响应 |

这些用例不调用 macOS 原生接口。原生读取需在 macOS 上实际运行 [前台状态程序](../../src/Observers/Heartbeat.Observer.ForegroundState/README.md)验证。
