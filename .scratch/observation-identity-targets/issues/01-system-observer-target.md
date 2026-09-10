# 01 — System 观测身份与设备归属完整链路

**What to build:** System 产生的活动 Segment 和输入 Event 经 Runtime 缓存、上传、Analytics 存储后，可以直接按设备 Target 查询，并辨认实际 Observer；重启与升级不重复或丢失事实。公共契约的必要扩展与这条可验证链路一起落地。

**Blocked by:** None — can start immediately.

Status: ready-for-human

**Parent:** [观测身份与事实归属改造](../PRD.md)

## 实施约束

Observer 是稳定的具体采集器，FOI 是直接观测对象；Facts 保留 Segment、Event、Measurement 的概念，本次只实现现有前两类。System 的 Target 是设备。Target 采用种类与引用表达，不增加五个角色列，不登记所有 FOI。

新事实直接携带 Observer 与唯一 Target。引用可以离线创建或继续使用，由 Analytics 解析为业务资料；不得把在线领取数据库行号作为采集前置条件。允许同一 Observer 为不同事实指定不同 Target。明确局部引用编码和持久化方案，并记录理由，供后续任务复用。

保留现有 Owner、Stream、FactId 身份组合和 Revision；Stream 退出业务归属，但仍可承担交付和内部事实身份。保留现有 SystemActivityModel，改变发布契约所需的状态与序列化，不重新设计业务活动判断。

追加迁移并兼容改造前第一方写入与缓存。第一批只回填有依据的 System 身份/归属，其余历史保持可读，为后续映射保留入口；不得伪造 Observer 或服务账号身份。兼容写入同一份家族事实，移除由任务 05 承接。

## Acceptance criteria

- [x] System Segment 与 Event 从实际发布入口经过 Runtime、Analytics HTTP 后能按设备查询，返回正确 Observer、Target、时间和原有 Payload。
- [x] 同一持久采集实例重启、停止恢复和更新后仍是同一 Observer；不同实例不因观察同一设备而合并。
- [x] 新字段经过实际使用的内进程/托管协议、持久缓存、上传及确认比较，更新期间不会被丢弃或错误确认。
- [x] 当前持久缓存升级后可重放；已有 FactId、Revision、时间、Payload 与行身份保持，重复上传收敛到原事实。
- [x] 同 Revision 的真实内容冲突、乱序修订和 Owner 隔离仍成立；已回填事实的等价旧形状重放不产生重复或无故冲突。
- [x] 使用追加迁移从已部署家族基线升级，保持已有表与事实内容；原有迁移测试继续验证其对应历史阶段。
- [x] 已迁移 System 查询直接使用 Target；Report 仍只统计明确的 System 活动，旧 Browser/VRChat 在迁移前仍可使用。
- [x] 记录新增公共契约、实际兼容消费者、缓存升级方式及任务 05 的退出条件，不引入 DataSource、通用 Objects 或 Aspect 注册层。
- [ ] 完成适用自动验证和 System smoke，记录真实覆盖范围；按仓库要求完成 review 与提交，剩余真实门禁如实保留。

## Validation

优先扩展 FactHttpTests 的 Runtime 重启、HTTP 摄入与公开查询链路，同时验证 Segment 和 Event。FactStoreTests 覆盖修订、事务与 Owner 隔离；真实 PostgreSQL fixture 验证已部署基线升级与旧快照重放。System 的平台活动规则沿用必要回归和可重复 smoke，不用只测 DTO 的测试替代完整链路。

代码修改遵循仓库 .NET 重构流程，以最小失败测试复现本次风险，再完成相关测试、构建与命名检查。验收证据写入本任务，未完成项保持未勾选。

## Comments

2026-09-10：按用户认可的五批方案建立；这是当前可直接开始的任务。


## 实施与验证（2026-09-10）

实现和自动验证已完成；真实窗口/输入权限与 Windows 现场 smoke 尚未完成，因此保持
`ready-for-human`，最后一项不勾选。未改变已确认业务设计，未部署或改写已有迁移。

- 局部公共契约、UUID/硬件引用编码、家族 Target 关联、实际消费者与任务 05 退出条件见
  [实施记录](../../../docs/architecture/system-observation-targets.md)。
- TDD：首条实际 System 发布→Runtime 重启→HTTP→公开查询测试因缺 Observer 字段失败后通过；
  v3 缓存和已部署家族迁移测试也先确认缺回填失败，再实现至通过。
- `FactHttpTests`：System Segment/Event 时间与完整 Payload、Observer/Target、设备查询、
  实例重启与停止恢复、同设备独立实例、公开可见性门；旧 v3 缓存修订/ACK/重放/Owner 隔离。
- `FactStoreTests`：同 Revision 的 Observer/Target 冲突、不同事实归属、跨 Target 修订与乱序、
  同 FactId 不同 Stream 不合并、Owner 隔离，原事务与 Report/非 System 规则回归。
- `ObservationTargetMigrationTests`：真实 PostgreSQL 从 NativeFactCustody 家族基线追加升级，
  表 OID 和行身份不变，时间/Payload/Revision 保留，native System 身份回填、legacy Observer 未知，
  重复迁移及等价旧/新快照收敛；旧固定列数测试仍停在原历史阶段。
- System 包更新、InProcess 消息/Revision 冲突、Managed stdio→重启缓存→上传与错误 ACK 比较均通过。
- 全 solution `dotnet test Heartbeat.slnx --no-restore`：13 个测试项目，1,278 项通过；
  Windows 测试在 macOS 上运行不等于 Windows 真机活动采集验证。
- Browser：108 项通过、TypeScript/Vite build 和 `node scripts/collector-contracts.mjs check` 通过。
  前端 `npm --prefix frontend run verify`：288 项通过、类型检查和构建通过。
- `dotnet build Heartbeat.slnx --no-restore` 与 IDE1006 命名检查通过（0 warning / 0 error）。
- macOS 当前构建 `--verify-startup`：hostStarted=true、System registered、正常起停。
  它只证明宿主组合，不替代真实窗口转场/输入刺激。
- 数据 smoke 的旧表引用已实际复现报错并修正；`node scripts/smoke-local-data.mjs check` 在
  现有家族基线数据上通过：222,400 活动段、1,918,845 输入事件；原有 9 组语义重复与 5,461
  条 System 重叠作为已有质量信号保留。此只读检查未部署本次迁移，不能当作新客户端水位推进。

### 剩余真实门禁

由 Owner 在隔离 Desktop Profile/数据副本中完成真实前台切换、停止恢复、输入权限与输入刺激，
确认两类事实经 Analytics 后 Observer 不变、Target 是对应设备、时间/内容正确；Windows 需在
Windows 环境重做对应平台检查。本次未以自动测试或 startup smoke 替代该验收。
完整生产副本迁移/切换由任务 05 承接，沿用已有备份与停写窗口，不由本次任务擅自部署。

### 命名收口与复核

用户确认“本次 System 范围内，语义已改变的字段都用新名称”。System 活动/经历响应使用
ObserverId、TargetKind、TargetId、TargetName，不再输出 subjectId/Kind/Name；C# 旧兼容字段
命名 LegacySubject*，旧 JSON 名仅服务尚未迁移事实。Dashboard 归组、筛选与标注同步 Target；
旧 Browser 通过已有 DeviceId 保留设备关联，不伪造持久 Target。OpenAPI 客户端已重新生成。
HTTP 和前端行为测试先复现遗漏，再通过新语义和旧 Browser 关联断言。

按 .NET 重构流程完成基线和命名 inventory；固定 Roslynator 的 rename-symbol dry-run 在
Workspace.TryApplyChanges 抛 NullReferenceException，未写文件。以引用清单逐处替换，并以
全 solution 构建、命名检查和跨项目完整回归确认。此工具异常不改变业务设计或序列化边界。

Standards/Spec 两轴复核已完成：Spec 无发现；Standards 指出的设备 UUID 解析重复已收敛至
DeviceService，Dashboard glossary 的旧归组定义已同步。真实平台门禁仍按上文由 Owner 承接。
