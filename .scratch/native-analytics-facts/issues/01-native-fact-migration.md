# 原生 Fact 摄入、持久保管与历史迁移

Status: ready-for-human

2026-09-09：Owner 已通过逐项讨论确认 [ADR-055](../../../docs/adr/055-fact-storage-by-family.md)
中的核心 Fact 模型，并授权 agent 替换迁移、提交及继续副本演练。分表实现、完整副本逐行核对、
受限数据库/Analytics 启动与重启已通过；实际整机/磁盘、部署流程和现场升级仍待验收。
下方旧版验证记录保留，最新证据见 Comments。Owner 已报告生产备份完成，并授权提交后自行先部署
Analytics；本地 CI 对应检查中 Analytics 525 项通过。当前提交包含演练修复及脚本，生产结果待 Owner 验收。
首次 Deploy Analytics（run 34324475362，ef94a3d）在测试阶段失败，部署 job 被跳过：
UsageServiceTests 的五项断言误要求保留 100ns，而存储契约为微秒；本地时钟未暴露这一差异。
把测试输入固定为带 7 tick 尾数后，本地稳定复现六项失败；断言改为明确期待微秒存储精度，
保留高精度输入覆盖，未修改业务代码或迁移。定向 23 项、Analytics 全量 525 项及部署流程
原有脚本检查 6 项全部通过；远端需对包含本修复的新提交重新运行工作流。

## 验收

- [x] 第一阶段：核对旧字段完整映射、历史身份衔接及必要行为边界，见 [映射文档](../migration-mapping.md)。
- [x] 第二阶段：替换未部署迁移与 EF 模型，接通家族摄入和 SQL 查询；独立旧库 fixture 验证单次迁移、原表 OID 不变、10 / 9 列和历史身份保留。
- [x] Analytics 原子接收 Subject/Stream/Fact/Gap；Owner 来自认证身份。
- [x] 同修订幂等与冲突、低修订忽略、高修订纠正、旧撤回消息明确拒绝和 Event 发生时间不变均有自动验证。
- [x] 在完整备份副本中按家族分表迁移历史，依最终保留语义逐行核对数据与重启后的数据。
- [ ] 实际安装的旧 Runtime / 旧缓存升级重放验收；自动 fixture 已覆盖无重复计时与不覆盖原生纠正。
- [x] Hub 原生持久上传，精确确认版本；断网、重启、容量与 Instance 移除不丢未确认记录。
- [x] Machine/Account/Person 身份与查询隔离正确；Account 不伪装成 Device。
- [x] Dashboard 结构化 Payload 正确展示历史/新 Browser URL，Recap depth 正确读取嵌套字段。
- [x] 分表方案的 .NET 与前端相关回归通过；契约和文档同步。
- [x] 线上克隆在受限数据库/Analytics CPU、内存下完成迁移、全量历史核对、重启并记录空间占用。
- [ ] 实际 1C1G 整机与磁盘余量、10 分钟部署整体停服预算、最近两份成功备份与未解决失败备份保留策略验收。
- [ ] 部署 owner 完成真实数据库备份迁移演练和已安装 Windows/macOS/Headless 升级 smoke。

## 验证记录

2026-09-09 实施准备与未提交改动审查（基准 e82193c）：

- Standards/Spec 两轴均确认旧部署草稿偏离已定要求：停服后备份无整体时限，健康等待 1800 秒；
  成功备份无两份保留策略。该草稿与依赖 Facts.LegacyRecord 的旧演练脚本已移至
  `.local/verification/fact-model-implementation-prep/retired-drafts/`，连同旧迁移/部署 diff 留存，
  不再作为可执行发布或分表验收入口。恢复原部署接线，CI 仅增加真实保留的本地脚本回归。
- 撤下未提交的通用 Facts 迁移性能修改，避免与独立 Schema 删除任务产生无意义冲突。
  保留刷新/启动职责分离、就绪诊断、迁移专用命令超时与请求超时恢复；后者仍不是
  10 分钟生产停服总预算，部署预算与备份保留必须在分表演练后另行实现和验收。
- 迁移超时测试改为依据 EF CommandSource.Migrations 注入真实慢语句，取消对 FactSchemas 表名的依赖，
  以便 Schema 删除后仍验证相同能力。
- 原始快照只读核对：38 条 App 映射差异分别是 mphelper/MPHelper 31 条、Updater/updater 3 条、
  GaomonTablet/GAOMONTablet 3 条、uninstall/Uninstall 1 条。ExpandAppIdentity 迁移已按标准化名称
  生成唯一身份并选择最小旧 AppId，ActivitySegments 保留的旧 AppId 尚未同步；当前 Report/Usage
  优先读取 AppIdentity.AppId。快照中 AppId 非空但 AppIdentityId 为空的记录为 0。
  因此新模型沿用 AppIdentity 路径，不会改变这些记录当前查询所用的应用归属。
  明细在 `.local/verification/fact-model-implementation-prep/app-mapping-audit.json`。
- `dotnet tool restore`、解决方案构建（0 warnings/errors）、IDE1006 命名检查通过。
  本地脚本回归 6 passed；DatabaseMigrationTests + FactMigrationTests 在独立 Testcontainers 中 4 passed。
  PowerShell 无本机运行验证，生产流程和新分表迁移尚未验收；原始快照仍停留 AskingWindowIdentity，
  220146/1891698 行，未启动原项目应用、未迁移、未部署。
- Schema 独立任务已完成：原提交 feb85bb 已以 ff3a152 整合到 `codex/fact-family-storage`。
  恢复实施准备改动时解决了四份文档冲突，代码无冲突；整合后的构建、命名检查、本地脚本 6 项、
  DatabaseMigrationTests + FactMigrationTests 4 项均通过。用户已授权开始核心 Fact 实现。

2026-09-09 数据取证入口修正：原 `refresh-local-data` 恢复后启动当前 checkout 后端，
日志证实自动执行 `20260908141403_NativeFactCustody`，成功那次耗时 69.0 秒。因此该次
同步完成后的本地库不是原始线上基线；早先样例库也包含本地新客户端写入，不能冒充纯线上数据。
现已将 Bash/PowerShell 刷新入口改为只恢复数据库、打印迁移历史、保持应用停止；
Bash 恢复失败回滚也不再启动应用。`start-local` 独立启动，明确提示后端将应用待执行迁移。
`python3 -m unittest discover -s scripts/tests`：6 passed（命令替身回归）；Bash 语法与
`git diff --check` 通过。PowerShell 已同步修改并审阅，本机无 pwsh，未运行验证。
等待 Owner 重新同步后只读核对；未重新刷新真实数据库，分表迁移与受限资源验收仍未完成。

2026-09-09 09:56（Asia/Shanghai）Owner 已用修改后的脚本重新同步，只读核对完成：
backend/frontend/headless 均为停止状态，迁移历史停于 AskingWindowIdentity，无 Facts/FactStreams。
原始快照 220,146 条活动、1,891,698 条输入，382,777,023 bytes（约 365 MiB）；
全库列定义与之前基线一致。975 条完整 Attributes 包装、38 条 AppId/AppIdentity 映射差异、
17 条 vrchat.account 活动；无缺失 Owner、倒置活动区间或越界 EventType。
两条设计样例从本次快照重新读取，与旧样例逐字段一致，完整字段 diff 已用本次数据重新生成。
证据在 `.local/verification/fact-family-production-snapshot/`；这些查询未写数据库，也未启动应用。
此次仅完成设计基线核对，分表实现、迁移耗时与 1C1G 资源验收仍待后续执行。

后续逐项审阅：Owner 要求删除旧的完整存储提案，避免预设字段继续影响核心 Fact 模型。
已删除该提案并清理引用，已确认决定统一记录于 ADR-055；事实内容哈希明确不存储。
前述原始快照取证仍有效，旧提案的迁移后样例不再作为目标验收依据。当前仍处于设计审阅。

以下是此前通用 Facts 实现的历史验证，不代表当前分表方案完成：

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

## 2026-09-09 后续修订

本文前述 FactSchemaContract/Schema 测试证据属于初始实现；格式治理由 [issue 02](02-remove-fact-schema.md) 退役。
生产迁移与现场升级门禁仍由部署 owner 承接，本次不更改原项目数据库。

## Comments

2026-09-09：本次完成第一步映射。读取当前实体、摄入/导入、Hub projector 与旧迁移测试，
并核对本地保存的 before-schema/profile 取证文件；未连接数据库。逐项覆盖旧 ActivitySegments
10 列和 InputEvents 6 列，目标仍为 ADR-055 的 10 / 9 列。
下一步优先用最小用例验证历史与原生的双向到达、首次接管 Revision=1 和跨 Owner/Stream；
不因删除 LegacyRecord 就预建替代档案、别名表或新状态机。
IsFinal 不落库与旧服务端终态检查不能同时原样保留，已单列责任调整建议；分表编码和回归尚未开始。
已标明旧 runbook 的通用表 SQL/Down 验证已被新方案替代，修正兼容台账中永久整行档案的过期承诺。
本轮只验证文档字段覆盖、链接与 diff 格式，不沿用旧测试通过数作为新方案验收。

2026-09-09：Owner 授权开始替换，第二阶段完成。

- `NativeFactCustody` 沿用未部署编号，直接改造旧表；已部署的前序 migrations 未修改。
  删除 ObservedFact/LegacyRecord/持久化双投影，Segment/Event 各自只存一份 JSON。
  ActivitySegment/InputEvent 为组合 SQL 查询结果，测试通过专用 fixture 构造家族记录。
- 复用确定性旧身份：保留迁移行 Id，接管后切到原生身份；原生先到时同样识别迟到缓存。
  Segment 候选按 UUID 身份编码的已知前缀范围缩小后再完整核对，无标题/活动时间猜测或别名表。
  两表的 Owner/FactId 普通索引支撑身份查找；实际大数据查询成本留给资源演练验收。
- Collection/Runtime 继续保管终态；Analytics 不保存 IsFinal，只按数据库微秒精度比较家族时间
  与 Payload。Gap 保留 tick 精度，包含 1 tick 缺口的回归通过。ADR 已同步责任变化。
- 升级 fixture 初始失败：旧布局没有 Segments/Events；替换后测试额外确认唯一待执行迁移、
  PostgreSQL 表 OID 保持、全部列/时间类型、历史未知内容与 CodeSet、首次原生 Revision=1
  接管、旧缓存晚到不 regrow、冲突回滚保留旧表。Down 改为明确拒绝有损逆转换，恢复使用备份。
- 新失败用例修复：旧缓存仅更新时间/标题时保留 Payload 未知顶层成员；SQL 与运行时必须
  按同样的 JSON 类型识别旧包装；无应用证据的 system Segment 完整保管但不进入应用统计。
  未为这些路径新增状态表或放宽身份规则。最后两个失败用例进一步确保旧接口不能把原生
  数据库行 Id 冒充旧上传身份，从而绕过接管规则改写原生记录；修复后全量回归通过。
  应用归并也已删除逐条改写旧 AppId 的逻辑。
- 最终验证：`dotnet build Heartbeat.slnx --no-restore --verbosity quiet` 0 warnings/errors；
  IDE1006 命名检查通过；`dotnet test Heartbeat.slnx --no-build --no-restore` 13 个项目
  1259 passed / 0 failed / 0 skipped（Server 524、Hub 310）。Frontend verify 274 项与构建、
  Browser 96 项与构建通过；Collector contracts check、EF has-pending-model-changes（无差异）通过。
  契约检查首次发现本地 Browser dist 与已跟踪 Package 不一致，按既有 build 流程重新生成后通过，
  未产生 Browser 源码或 Package 的提交差异。日志与汇总在
  `.local/verification/fact-family-replacement/final/`。
- Friction closeout：runbook 已替换旧 Facts/LegacyRecord SQL 和过时 Down 说明；兼容台账记录
  档案退役及旧缓存退出门槛；EF 设计时工厂不执行应用启动或读取部署凭据。构建产物与原库隔离。
  原始快照数据库未连接、未启动、未迁移；未提交或部署。完整数据 diff、1C1G/磁盘/停服预算、
  两份成功备份保留与真实安装 smoke 仍未验收，issue/PRD 保持 ready-for-agent。

2026-09-09：Owner 要求先提交、继续下一阶段，并将长 `migrationBuilder.Sql` 拆开。

- 已提交替换为 `ae30045`（`refactor(facts): store segments and events directly`）。依 ask-matt
  阶段边界规则继续同一任务；下一阶段直接使用已有映射与验收约束。
- 原本地快照仅执行只读 pg_dump，完整备份 61,185,274 bytes，SHA-256
  `801e5503799f78e7ff6fdc1d67953dd52ae67652948f3187fb30f52e75c2af90`。
  来源库没有迁移，应用保持停止；恢复与升级均在新建的 internal 网络容器中执行。
- 缩小后的迁移仍在 Events 唯一索引处触发 512 MiB OOM，纯数据库复现也失败。
  建索引前 PG 内存上下文约 4 MiB、触发器队列 8 KiB，未见历史更新持续积压。
  单变量关闭并行建索引后迁移通过，因此只保留迁移内
  `SET LOCAL max_parallel_maintenance_workers = 0`，不修改全局配置。
- 长 SQL 拆为预检、改表、身份回填、Segment 转换、Event 转换、约束收尾六个命令，
  EF 仍用同一事务；新增晚期约束失败用例，确认两类转换完成后失败也恢复全部旧表/旧行。
  迁移相关 8 项测试通过；此前串行索引修复的 Server 全套 524 项通过，EF 模型无差异。
- 迁移通过后，最初核对脚本的一次全历史关联/排序读取另有 OOM；关闭查询并行、降低建索引
  内存和 JIT 对照均没有完成全量核对。Owner 提醒回到迁移范围后，撤下这些无效尝试。
  正式入口改为 Id 游标每批 10,000 行，先限制行数再关联/序列化；比较总行数必须等于来源
  计数，仍逐条检查全部内容。未保留 JIT、排序内存或查询并行配置改动。
- 最终入口为 `scripts/rehearse-fact-migration.py`；完整证据在
  `.local/verification/fact-family-rehearsal/run-05/`（`report.json`、启动日志、私有逐行导出）。
  候选镜像为 `heartbeat-fact-rehearsal:split-sql`，image id
  `sha256:2e319298d8750df7ffb999a9cf8014c725441e2dc75d024264e8c3f71d464375`；构建复用缓存的 .NET 10 SDK，具体 Dockerfile/build 日志
  保存在同级本地目录。运行平台 linux/arm64；没有把 Docker CPU 配额等同于真实服务器性能。
- **结果：通过。** PostgreSQL 0.75 CPU / 512 MiB、Analytics 0.25 CPU / 256 MiB，禁用额外
  swap。迁移 37.4 秒；受限备份 4.569 秒，备份至健康共 48.337 秒；重启至健康 5.534 秒。
  220,146 条 Segment、1,891,698 条 Event，以及 5,143 / 6,746 条聚合结果均零差异；
  重启后再逐条核对全部事实，仍零差异。表 OID 保留、10 / 9 列、无 Facts，迁移日志仅应用一次。
- 数据库从 382,482,111 增至 1,637,381,823 bytes；迁移 WAL 增量 2,238,203,400 bytes。
  就绪/重启探测期间 PostgreSQL 目录采样最大约 2.653 GiB（含 WAL，非全过程磁盘硬峰值）；
  DB working set 采样最大约 457.41 MiB，Analytics cgroup 内存峰值约 74.24 MiB；最终无 OOM。
  原地 UPDATE 仍有死元组/索引/临时文件成本，不能按旧库大小直接安排磁盘；尚未做空间回收或生产磁盘验收。
- Friction closeout：入口能明确失败/成功，所有记录都核对且保留私有证据；没有永久档案或全局
  调参补丁。runbook/映射/PRD 同步。此轮拆分、局部资源修复与演练脚本尚未提交，未部署。
  实际整机/磁盘预算、部署停写/备份保留流程和真实客户端升级仍由后续阶段承接，保持 ready-for-agent。
