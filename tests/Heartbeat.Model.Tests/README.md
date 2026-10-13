# Heartbeat.Model.Tests

无需数据库的实际 Core/Application 规则测试：

```sh
dotnet test --project tests/Heartbeat.Model.Tests/Heartbeat.Model.Tests.csproj
```

Fixtures 包含100个语义场景、18个有效/无效对照、6个实际本地采样记录。每条保留来源、证据类别、声明、内容和预期。大部分值按官方文档构造，不是生产采集结果。ScenarioTests直接调用当前EntitySchemaRules。

JsonAmbiguityTests检查重复解码字段名。SchemaBoundaryTests检查数值、缺失/null、观测身份；整数等价测试包含8004组检查，不计作8004场景。

Integration.Tests/ModelScenarioTests复用100场景、4有效对照、6采样记录，检查真实HTTP/PostgreSQL、Schema发现、完整读回、重复提交。A/C组50份时间封装执行读回比较，其余文字时间映射不冒充可执行时刻。

详细设计与来源的权威文档在 src/Docs/Heartbeat.Docs/content/docs/testing/model-generality/。

## 原生来源与本机运行时

`NativeFixtures/` 是 Graph、Slack、Gmail、OTLP、CloudEvents、Debezium、ICS、GPX 的合成原生输入，不是归一化的 `Fixtures/`。对应来源测试直接调用实际适配器；`LocalSourceTests` 另创建隔离 Git/文件样本并读取本机 procfs。Linux 文件、进程检查有平台边界。

`SourceRuntimeTests` 检查 SQLite 身份/历史/事务、重启与读回；`SourceCliTests` 运行实际 CLI 子进程，检查干净失败和离线重放；`SourceCoverageTests` 保证 Observers 清单恰好覆盖原始 100 个编号及有效适配器。通过这些测试不代表 100 个来源均已原生接通。

来源运行方式与限制见 [Sources README](../../src/Observers/Heartbeat.Observer.Sources/README.md)，权威说明见[来源验证](../../src/Docs/Heartbeat.Docs/content/docs/observers/sources/verification.mdx)。
