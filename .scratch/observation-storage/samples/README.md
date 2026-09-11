# 存储迁移取样

入口：[可读 diff](REPORT.md)。原始快照为 [before.json](before.json) / [after.json](after.json)。

这些是构造样本在隔离 PostgreSQL 中真实迁移后的查询结果，不包含生产数据。
通过现有 PostgresContainerFixture + PostgresTestBase 执行，测试结束自动删除临时数据库及容器。

- `seed.sql`：只适用于已迁到 CompleteHistoricalTargets 的隔离空库。
- `capture.cs`：本次运行的临时取样 harness，复用现有 migration fixture；保存在这里，不加入正式测试程序集。
- `cases.json`：9 个事实的标签与固定 Id，不是 migration 映射表。
- `inspect.py`：读取两个已导出的 JSON，比较完整事实、原资料和关系的具体成员/时间，生成 REPORT.md；不连接数据库。
- 验证日志：本次 `/tmp/heartbeat-storage-inspection.log`，取样测试 1/1 通过。

离线重新核对：`python3 .scratch/observation-storage/samples/inspect.py`。
需要重新采集时，可由 Agent 将 capture.cs 临时放到服务端测试目录，运行该测试后移除；
其中输出目录为本次工作区绝对路径。不能将 seed.sql 用到业务库。
