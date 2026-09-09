# 原生 Fact 摄入、持久保管与历史迁移

Status: ready-for-human

## 验收

- [x] Analytics 原子接收 Subject/Stream/schema/Fact/Gap；Owner 来自认证身份。
- [x] 同修订幂等与冲突、低修订忽略、高修订纠正、旧撤回消息明确拒绝和 Event 不可变均有自动验证。
- [x] 历史 ActivitySegment/InputEvent 保留原始档案并导入，新契约 Runtime 重放无重复计时；旧缓存不会覆盖原生纠正。
- [x] Hub 原生持久上传，精确确认版本；断网、重启、容量与 Instance 移除不丢未确认记录。
- [x] Machine/Account/Person 身份与查询隔离正确；Account 不伪装成 Device。
- [x] Dashboard 结构化 Payload 正确展示历史/新 Browser URL，Recap depth 正确读取嵌套字段。
- [x] 全量 .NET 与前端相关回归通过；契约和文档同步。
- [ ] 部署 owner 完成真实数据库备份迁移演练和已安装 Windows/macOS/Headless 升级 smoke。

## 验证记录

实现及自动验证完成；owner 已授权 review 修复后提交，未修改真实用户数据库。剩余真实备份/安装升级门禁由部署 owner 承接，
按 [升级核对步骤](../../../docs/runbooks/native-analytics-facts.md) 记录现场证据后才能置 done。

- `dotnet build Heartbeat.slnx --no-restore`：0 warnings / 0 errors。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- 最终解决方案回归覆盖 13 个测试项目，共 1249 项：Server 497、Hub 330、Core 39、Protocol 42、
  System 89、Mac 82、Windows 38、UI 62、Updater 11、Headless 14、VRChat 21、Reference 2、Verification 22。
  其中既有 `DrainWriteDisconnect_IsFailedAndWriterIsReleased` 在并行全量执行时一次失败；
  随后的单项及完整 Hub 330 项复跑全部通过。其他项目此次全量全部通过；不将最初总命令记作单次全绿。
  尚无证据把该波动归因于 Fact 变更，未通过放宽断言掩盖。
- 新增 `FactStoreTests`、`FactMigrationTests` 与 `FactHttpTests`：覆盖真正旧 PostgreSQL 数据迁移、
  原始档案与未知属性、输入 CodeSet/Code、历史接管、旧缓存晚到、跨 Owner 同 identity、
  Account/Person 查询、schema/hash、HTTP 401/200/409 与整批回滚。
- Hub native tests 覆盖断网容量/重启、精确 revision/hash 确认、纠正、Gap 身份/alias 迁移、
  待传阻止删除、409 持久隔离、硬件 UUID 大小写与 backlog 状态。
- 前端 `npm run verify`：40 files / 271 tests、typecheck、build 通过；NSwag 客户端重新生成。
- `node scripts/collector-contracts.mjs check`、`git diff --check` 与本轮新增文档链接检查通过。

## Friction closeout

- 历史 Attributes 包装兼容已移入显式导入边界；生产新 Fact 不再写旧持久投影。
- 旧缓存/旧嵌入式 harness 的对象、退出条件和验证方式已登记兼容台账，未把它们当永久上传接口。
- Native 接收后有损 Down 明确拒绝，legacy-only 迁移可无损 roundtrip；原始归档不随兼容窗口删除。
- 真实数据库启动会自动执行 migration；现场升级步骤与 owner 门禁已记录，未把 fixture 通过当作上线完成。

## Review 修复与提交验证（2026-09-08）

- 最小回归先失败：`dotnet test server/Heartbeat.Server.Tests --no-restore --filter FullyQualifiedName~FactStoreTests --verbosity quiet`
  → 7 failed / 11 passed；`npm test --prefix frontend -- src/segmentAdapters.test.ts` → 2 failed / 15 passed。
- Fact/Gap 原生时间改为 UTC ticks 无损持久化。真实 PostgreSQL 跨 DbContext 回归验证 Start/End/ObservedAt、
  1 tick Gap、重复提交与更高 Revision，并证明原生 Start 即使只改 1 tick 仍被拒绝。旧 InputEvent 接管仅按
  历史 Npgsql 微秒编码核对，接管后保留原始 native OccurredAt，不把容差扩散到原生身份判断。
- Shared Kernel `FactSchemaContract` 统一 Package/Analytics 的文档、演进、引用校验；覆盖本地 anchor、
  嵌套 `$id`、annotation 内字面 `$ref`。Schema 原始 hash 只校验运输完整性，同版本文档按语义比较；
  换行不冲突、同版本实际改义仍拒绝。
- Legacy 接管只查 `legacy-import` 候选；三个 Event Stream 重用同一 FactId 均保持独立。
- Browser 泳道使用 Stream + windowId；历史导入缺少原始 Host 身份时按区间装箱，不使用合成 Legacy Stream
  假造窗口归属。旧投影写算法移入 FactStore.LegacyImport，移除查询服务间的双向调用。
- `dotnet build Heartbeat.slnx --no-restore --verbosity quiet` → 0 warnings / 0 errors。
- `dotnet test Heartbeat.slnx --no-build --no-restore --verbosity quiet` → 13 projects / 1255 passed / 0 failed / 0 skipped；
  Server 503、Hub 330、Core 39、Protocol 42、System 89、Mac 82、Windows 38、UI 62、Updater 11、
  Headless 14、VRChat 21、Reference 2、Verification 22。本轮完整命令一次通过。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore --verbosity quiet` 通过。
- `npm run verify --prefix frontend` → typecheck / 274 tests / build 通过；Browser 96 tests / build 通过。
- `node scripts/collector-contracts.mjs check`、`git diff --check` 通过。原生 migration 为尚未发布的新 migration，
  直接修正其建表与历史导入；真实旧 PostgreSQL Up/Down roundtrip fixture 通过，无真实库变更。
- 本轮代码完成；真实备份迁移、已安装 Desktop/Headless 升级与线上到达证据仍未执行，维持 ready-for-human。

## 删除 Fact 撤回（2026-09-09）

- 按 owner 决策移除 Fact RecordState、撤回枚举、Schema 开关、墓碑及投影撤回接口。
  正式 System/Browser/VRChat 仅产生普通快照；保留身份、正常修订、finality、乱序保护、
  同版本幂等/冲突及 Event 家族规则。五个 schema 提升至 Major 2，客户端与 Package 引用同步。
- 替换撤回专属测试；迟到确认与旧缓存交错改用更高 Revision 缩短区间验证。
  Hub wire 与 Analytics HTTP 均覆盖旧撤回有无 payload 的明确拒绝和原事实不变；
  Shared Kernel 覆盖 Segment/Event 反序列化拒绝以及旧 Schema 开关拒绝。
- `dotnet build Heartbeat.slnx --no-restore`：0 warnings / 0 errors；命名检查通过。
- `dotnet test Heartbeat.slnx --no-build --no-restore`：13 个项目 / 1257 passed，0 failed / skipped。
  Server 503、Hub 326、Core 45、Protocol 42、System 89、Mac 82、Windows 38、UI 62、
  Updater 11、Headless 14、VRChat 21、Reference 2、Verification 22。
  此后追加的 HTTP 拒绝测试通过 `--filter FullyQualifiedName~FactHttpTests`：3 passed。
- 前端 typecheck / 274 tests / build、Browser 96 tests / build、schema `check --base-ref HEAD`
  及 `git diff --check` 通过。Hub 首轮旧 fixture major 不匹配已修正，最终全量通过。
- PostgreSQL 验证使用 Testcontainers 随机容器/端口和独立 test 数据库；未启动原栈，
  未碰原 `.local/postgres-data`、线上快照、原工作区，也未部署、推送或触发 GitHub CI。
- Friction closeout：更新 ADR-041/054、glossary、契约和升级文档；不实现分表、时间列、Id/FactId
  或 Revision 起点的独立设计。含旧字段的 Runtime/outbox 明确拒绝且保留文件，没有新增转换器；
  现场持久状态核对与无损切换仍由部署 owner 承接，禁止删状态绕过拒绝。
  本次删除功能及自动验证完成；原生升级 issue/PRD 仍为 ready-for-human。
