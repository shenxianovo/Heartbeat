# 04 — 本人关联维护与追溯查询

**What to build:** 用户在 Heartbeat 中明确维护设备或账号与本人的关联及适用时间，然后通过本人视图查询对应的已有事实。添加、纠正或移除关联会改变查询结果，原始 Facts 保持不变。

**Blocked by:** [02 — Browser 窗口观测与应用上下文归属](02-browser-application-context.md); [03 — VRChat 账号观测与事实归属](03-vrchat-account-observation.md).

Status: done

**Parent:** [观测身份与事实归属改造](../PRD.md)

## 实施约束

关联由 Heartbeat 独立维护，存放在 Facts 之外，先实现明确的设备/账号使用者关系，不建立通用关系图。关联包含经确认的适用时间范围；当前 Owner 的设备/账号不会自动成为本人全部历史的证据。

本人查询纳入直接以本人为 Target 的事实，以及适用范围内关联设备与账号的事实；应用上下文通过所属设备参与。保留原事实 Observer 与唯一 Target，查询关联不产生第二份事实，也不改变 Revision。

部分覆盖 Segment 时仅以关联覆盖区间展示或计算，保留原始起止时间；Event 按发生时刻判断。重叠关联匹配同一事实时去重，多个不连续的有效区间不得被扩大成连续覆盖。本人视图不把 Browser、VRChat 与 System 时长简单相加当作注意力。

## Acceptance criteria

- [x] 通过现有管理入口的最小设置交互可以创建、纠正及移除设备/账号与本人的时间范围关联。
- [x] 按本人筛选会返回个人 Target、关联设备、其应用上下文及关联账号的适用事实；离线 Collector 的历史也可返回。
- [x] 今天补录过去的关联后，已存历史按适用范围出现在结果中，无需重传或改写事实。
- [x] 部分覆盖 Segment、时刻 Event、重叠关联和不连续关联的结果符合时间语义，事实与统计不因关系 join 重复。
- [x] 纠正或移除关联只改变查询；原 FactId、Observer、Target、Revision、时间和 Payload 保持不变。
- [x] 没有明确关联的设备/账号不因属于当前 Owner 就自动算作本人；不同 Owner 无法读取或修改对方关联及事实。
- [x] 用公开维护入口到查询/视图的链路验证，不以关系表 CRUD 测试代替业务验收。
- [x] 交互、自动验证及适用构建结果已记录，完成 review 与提交；不引入通用对象管理界面或 Measurement 采集实现。

## Validation

通过维护接口写入关联，再从公开查询读取预先存在的 System、Browser、VRChat 事实与个人 Target fixture。覆盖追溯、部分重叠、移除、去重与 Owner 隔离，并对照原事实未被修改。用最小 UI smoke 确认用户能够完成关联维护及筛选。

如果现有本人视图必须新增查询接口，以本批真实交互所需为边界；不额外建设通用关系查询语言。

## Comments

2026-09-10：应用上下文与账号链路均完成后，可验证本人视图覆盖当前已接入的业务。


2026-09-10 实施完成：具体身份、存储、时间、查询及迁移方案见
[本人关联实施记录](../../../docs/architecture/person-observation-associations.md)。
开工 HEAD 为 `996264b`；保留用户原有 ADR-056、模型文档、shared/CONTEXT.md 和 Ticket 05 草稿，
本任务提交仅包含实现、相关说明及本票据/PRD 收尾。


### 自动验证

- TDD 从公开维护/查询入口推进：先摄入已有 System、Browser、VRChat 与直接个人 Target 事实，
  再补录关联；覆盖部分交集、开放边界、Event 起点包含/终点排除、重叠去重、不连续区间、
  纠正/移除、稳定分页与计数、跨 Owner 拒绝，并逐字段核对原事实未改写。
  个人 Target 另外经过 Runtime 保管、重启、HTTP 摄入及重放，Segment/Event 均验证。
- 真实 PostgreSQL 18 验证增量迁移及重复升级：家族表 OID、行身份与完整原始行不变，
  不自动建立 Person 或历史关联；检查 Owner 复合外键、有限区间、不可变个人身份和删除引用保护。
  最终个人 HTTP/Runtime/迁移测试 15 项通过。
- Review 新增的两个旧 Repeatable Read 快照回归先失败（摄入提交后仍可删除 Person），
  修复后要求 serialization failure 并保留 Person/Fact。前端同毫秒内微秒区间纠正也先复现失败，
  修复后将原始 ISO 原样送回，由服务器验证精度和区间。
- 开工基线：解决方案构建、IDE1006 检查通过，Mac 测试 82/82 通过；这不否定 03 已记录的退出波动。
- 本批首次完整 .NET 回归为 **1317/1318**：
  `MacAgentHostExtensionsTests.ApplicationExit_WithRealHostAndOfflineDurableTail_Completes`
  在 10 秒退出等待超时，与 03 已记录的既有问题一致。没有修改退出实现或降低断言。
- Review 修复后重新构建及完整回归：`dotnet build Heartbeat.slnx --no-restore` 零警告、零错误；
  `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 通过；
  `dotnet test Heartbeat.slnx --no-build --no-restore -m:1` 为 **1320/1320**（增加两项并发回归）。
  该复跑结果不代表 macOS 波动已修复，也不将本轮所有运行描述为全套全绿。
- 前端类型检查、生产构建通过，44 个文件共 **294** 项测试通过；Browser 14 个文件共 **111** 项通过。
  暂存区 whitespace 检查通过。
- 本机日志：`/tmp/heartbeat-ticket04-full-dotnet.log` 保留首次失败；最终结果为
  `/tmp/heartbeat-ticket04-final-{build,naming,dotnet,types,frontend,frontend-build}.log`，
  Browser 为 `/tmp/heartbeat-ticket04-full-browser.log`。定向测试与红灯证据保存在同前缀日志中。

### 实际交互验收

运行 `node scripts/smoke-person-associations.mjs`，使用真实 Chrome headless、当前构建的 Dashboard
与 Analytics、隔离 PostgreSQL。先通过 HTTP 存入四类 Target fixture，再通过设置入口的真实表单
创建设备/账号关联、纠正范围、移除关系和切换事实筛选；核对有效覆盖与原始 Facts 全部不变。
最终运行完成于 `2026-09-10T15:03:02.946Z`，退出 0；
[脱敏报告](../person-smoke-report.json) 中全部检查通过，截图已目视检查。
本地截图为 `.local/observation-person-04/person-view.png`，日志为
`/tmp/heartbeat-ticket04-final-smoke.log`。凭据不进入日志或事实。

这是实际浏览器控件与真实服务的交互验收，数据为可控 fixture，不代表真实 Collector 或真实账号验收。
没有写日常数据库或执行生产部署；临时服务、容器和浏览器 Profile 已清理。

### Standards / Spec review

固定基线 `996264b`，两位独立审查者并行检查本票据暂存实现（排除开工已有改动）。
Standards 初审发现手写 DTO 权威重复、客户端 Date 丢失微秒导致错误拒绝、手工维护端点表三项，
已分别改为生成契约派生 JSON 类型、保留 ISO 并由服务端判断、链接 Controller/OpenAPI；复审 **0 项待处理**。
Spec 初审发现旧事务快照下个人引用保护存在并发删除漏洞，已补红灯测试并以 Person.Reference
同值 UPDATE 产生 MVCC 行版本修复；复审 **0 项待处理**。
独立 SQL 验证覆盖 Read Committed / Repeatable Read / Serializable 与三种摄入/删除顺序的
9 个组合，均无悬空 Fact。改动仅扩展本批个人 Target，未宣称修复或验证旧 Target 的所有并发路径。

### Lifecycle / friction closeout

04 的实现、自动验证和最小实际交互验收完成，状态为 done；提交采用
`feat(person): add time-scoped observation associations`，在当前分支提交本票据范围。
PRD 保持 ready-for-agent，下一开发承接为 05；01 的真实 System/Windows、03 的真实 VRChat 账号
仍为 ready-for-human，05 的生产副本迁移演练及切换门禁未完成，本批不替它们勾选。

本批实际发现的契约/文档重复已消除；新增 smoke 脚本有明确退出码、脱敏报告和清理步骤，
本人查询没有引入旧归属回退或新的兼容适配器。既有 macOS 退出波动继续以 Ticket 03 证据为依据，
后续需针对退出生命周期定位并验证；本批复跑通过不足以关闭它。不另建重复 tracker。
开工已有未提交与并行文档草稿保持原样，不混入本任务提交。
