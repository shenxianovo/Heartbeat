# 01 — 独立观测保存与读取

**What to build:** 完整的新 Segment/Event 不提供旧 Subject/Stream，也能经真实 HTTP 保存、重传、正常修订并从公开查询读取。生产者的稳定 Id 是新事实的存储身份；持久化和验证由观测契约决定。

**Blocked by:** None — can start immediately.

Status: done

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [x] 先用真实 HTTP、PostgreSQL 和查询 fixture 稳定复现：无旧 Subject/Stream、无关系的完整观测当前不能独立存取；记录失败原因，不能在测试或入口内伪造 Stream。
- [x] 新原生 Segment/Event 自身声明 Id、Kind、Collector、FOI、Aspect、Result、Time、Revision，Owner 取认证身份。读回 Id 与生产者 Id 相同，空 Relations 合法，未知 Aspect 和未知 JSON 字段完整保管。
- [x] 同步扩展请求/响应、实体、数据库约束及原始查询，旧交付键对新事实不再必需；机器、App、账号、个人按各自合法身份引用可独立存取，不要求补造设备关系。
- [x] 新原生输入缺少有效 Collector/FOI/Aspect 时明确拒绝；历史 null 的存储能力不会放宽原生验证。对象解析使用真实身份及既有产品规则。
- [x] 同一新 Fact 的 Observer、实际 FOI、Kind、Aspect 固定，更高 Revision 也不能更换；Segment 起点和 Event 发生时刻固定。App 目录身份解析与维护沿父规格的独立规则处理。
- [x] 同版本相同完整语义幂等、不同内容冲突，低版本不覆盖高版本；合法高版本可更新 Result、延长或缩短 Segment 及更新关系，保持 Id。
- [x] 完整 Fact 与其关系原子提交，失败不留下部分事实或成员；关系绑定准确 Fact，角色/种类/基数与 Owner 有保护，无关系事实也能读取。
- [x] Collector、私有对象、关系引用与查询保持 Owner 隔离；独立事实 Id 碰撞不覆盖已有行，跨 Owner 响应不泄露他人内容。
- [x] 新旧入口收敛到同一事实写入核心和唯一 Facts；现有生产者、旧行身份与旧入口在扩展阶段仍可使用。追加迁移保住已有数据，旧身份/缓存的完整升级矩阵由 03 承接。
- [x] 原始读取完整支持新事实；领域对象维护由 07、派生分析和 Dashboard 全面切换由 08 承接，不能把这些后续范围提前声明完成。
- [x] 对应 HTTP、存储和迁移回归通过，记录可重复命令、结果与未验证范围；本任务不操作业务库或恢复暂停的生产演练。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。

### 2026-09-11 实施接口与兼容边界

- 新入口：`POST /api/v1/observations`，认证及 Heartbeat 协议头沿用旧入口；请求为 `ObservationUploadRequest { facts: ObservationSnapshot[] }`。
- 每条 Snapshot：`id`（生产者稳定非空 UUID）、`kind`（segment/event）、`collectorId`、`foi`（kind/scope/key）、`aspect`、`result`、`revision`；Segment 使用 `start/end`，Event 使用 `occurredAt`。`source` 可空；`relations` 默认空数组。认证身份决定 Owner，不接受旧 Stream/Subject 骨架。
- `GET /api/v1/users/{username}/facts/segments|events` 保持原始读取入口，增加 `kind/result`；`payload` 是相同结果的兼容读别名，不增加第二份存储。新事实的 `id` 为生产者 Id，旧 `streamId/factId/source` 可空。
- 02：HTTP ACK 是原子请求成功；Runtime 必须保管 `IsFinal`/本地终态及准确发送快照，不能把无 `IsFinal` 的持久契约误解为取消终态或 ACK 完整比较。
- 07：保留平台 `AppIdentityId` 证据；目录 merge/rebind 独立改变 FOI/关系引用，保持 Id/Revision/Result/Time。直接产品引用解析与关系引用均沿用现有 App 目录。
- 08：FactRecord、ActivitySegment、ExperienceSegment 和原始 FactResponse 的旧交付键可空；Source 未知保持 null，派生分析/前端不得依赖 Stream 或以缺失 Source 猜测 Aspect。
- 保留兼容消费者：当前第一方 Runtime 上传及历史缓存仍使用 `/api/v1/facts`，旧导入仍使用既有入口，经同一 `SaveSnapshot` 提交 Facts/Relations。02/04/05/06 切换活跃生产路径、03 验证完整缓存与历史身份矩阵后，按实际安装窗口和待发缓存清空证据决定旧入口退出；本项不提前删除兼容。
- 人工与跨票范围：不部署、不碰业务库、不恢复暂停的生产演练；02–09 及父 PRD 的整体验收由协调任务承接，本项只声明独立存储与原始读取。


### 2026-09-11 验收与审查收口

共同基线：`e189ccf0e0e805026a557c8ab18c2e91c14868ba`，工作分支：`codex/observation-ticket-01`。
本项完成；父 PRD 和 ORCHESTRATION.md 不由本工作分支修改。

失败→通过证据：

- 最小真实 HTTP + PostgreSQL + 原始查询测试最初 Segment/Event 两项失败（独立入口尚不存在）；实现后生产者 UUID、未知 Aspect/完整 Result、空 Relations 和原始读取通过，未伪造 Stream。
- 真实旧 schema 下独立 INSERT 明确触发 `23502`，追加迁移后成功；重复升级保留旧 UUID、Revision、完整 JSON 和微秒时间，历史 AppReferenceEvidence 保持 null。全部迁移测试通过。
- 审查补出的旧导入同/跨 Owner、同/跨 Kind Id 冲突及非有限时间先稳定复现 500；修复为明确 409/422，原事实不变。低版本无效 FOI 原为 200，修复为 422，忽略旧版本不注册新对象。
- 平台身份切换到产品引用后，目录重绑的准确原快照重放先复现 409；保存最小 AppReferenceEvidence 后，同版本/高版本的原样重放通过，任意替换 FOI 仍冲突。
- 机器 FOI 合法替换或移除 App 关系的三个测试先全部失败；只让平台身份维护仍适用的当前引用后全部通过，旧 App 重绑不污染新关系。平台身份本身不被错误加入 Fact 的四项固定字段。

可重复验证（均使用隔离 PostgreSQL 测试库，无业务库操作）：

- `dotnet restore Heartbeat.slnx`、`dotnet build Heartbeat.slnx --no-restore`：成功；最终全仓构建 0 errors。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- `dotnet test Heartbeat.slnx --no-build --nologo`：全仓 1,367 项通过、0 失败、0 跳过。最后三项关系修复之后，按改动范围补执行下面两组服务端回归，不重复未改动的 Collection 项目。
- `dotnet test server/Heartbeat.Server.Tests --no-build --filter 'FullyQualifiedName~FactHttpTests|FullyQualifiedName~FactStoreTests|FullyQualifiedName~ObservationStorageTests|FullyQualifiedName~MigrationTests' --nologo`：最终 132/132 通过。
- `dotnet test server/Heartbeat.Server.Tests --no-build --filter 'FullyQualifiedName~FactHttpTests|FullyQualifiedName~FactStoreTests|FullyQualifiedName~ObservationStorageTests|FullyQualifiedName~AppCatalog|FullyQualifiedName~AppMerge|FullyQualifiedName~AppIdentity|FullyQualifiedName~BrowserApplicationContext'`：最终 154/154 通过。
- 最终独立关系专项 8/8 通过，覆盖新增的三个失败→通过场景；`git diff --check` 通过。上述回归组有重叠，不将其简单相加充当总测试数。

双轴 code-review：Standards 的 4 项兼容/时间/API 边界发现已修复并复核关闭；Spec 的 3 项引用重放、原生验证与关系证据发现已修复并复核关闭。两轴均无未关闭发现。

Friction 与后续边界：

- 编译暴露的 nullable Source 消费边界（UsageService、DigestAssembler、QuestionService、PersonFactQuery 等）已通知协调任务交 08；本项原始读取如实返回 null，不声明派生分析或 Dashboard 已全面切换。
- AppReferenceEvidence 只保存已接受的精确 App 引用位置/原引用/平台身份。删除或替换关系会撤去旧平台身份对当前引用的维护权；不新增历史 Result 档案。07 维护不得丢弃仍适用的引用证据。
- 本机全局 `/Users/bytedance/.bytesec/commit_hook/pre-commit` 在一次提交时报告硬编码扫描退出码 2，按其自身非阻断规则放行；未绕过或修改该 hook，扫描不计入通过证据。仓库规定的构建、测试、命名和双轴审查已完成。
- 无本项未完成验收或人工门禁；Runtime/缓存/生产者/全面消费的 02–09 验收由对应工作项继续承接。部署、真实安装、业务库迁移与暂停的生产副本演练均未执行。
