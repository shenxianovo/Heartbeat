# 02 — 原生观测的持久保管与交付

**What to build:** Collector 使用公开 SDK/协议发布独立 Fact，Runtime 在离线和重启中持久保管，通过真实 HTTP 写入 PostgreSQL 并可查询；上传确认只确认准确发送的快照。

**Blocked by:** [01 — 独立观测保存与读取](01-independent-observation-custody.md).

Status: done

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [x] 经公开 SDK/Collector Protocol 的最小发布用例贯通 Runtime、HTTP、PostgreSQL、读取与 ACK；新事实不靠生成旧 Subject/Stream 才能提交。优先复用现有 transcript/HTTP fixture。
- [x] 扩展共享契约及受支持执行方式的发布/初始化接口，使事实自身携带身份、Kind 与观测信息；保留尚未迁移调用方的可用形式，各批迁移可分别保持回归通过。
- [x] Runtime 与服务端一致验证原生完整性及同 Fact 固定 Observer/FOI/Kind/Aspect；保留家族时间、完整 Result、Relations 与单调 Revision，不改造成旧活动投影再交付。
- [x] SDK/outbox 与 Runtime 在停止、重启、重试后保留 Fact Id、版本、内容、终态和待发状态；新格式可持久恢复，测试观察真实重启后的发布或待发结果。
- [x] 上传期间产生更高版本，再收到旧 ACK 时，新版本仍待上传；同一发送版本也必须核对完整快照。最终确认后再次重启，不重复保留已确认版本或丢失更新版本。
- [x] 乱序、重复及同版本冲突在 SDK/Runtime/服务端交互中正确处理，失败不会被误确认；正常 Segment 增长、缩短和收尾遵守既有语义。
- [x] Gap 的实际丢失范围、身份及确认保持，重启重放不增加或遗失 Gap；交付需要的分组由交付自身保管，不决定 Fact 的 FOI、Kind 或身份。
- [x] 协议协商、缓存版本及包启动/回退保护同步扩展，不理解新契约的包不能处理新缓存或静默 ACK 新事实；失败保留可恢复状态。
- [x] 本项落实通用新链路和兼容入口的明确接口；完整旧数据升级/接管由 03、三个真实生产者与专有状态迁移由 04–06 验收，不复制永久双写链路。
- [x] 对应协议、outbox、Runtime、HTTP 集成回归通过并记录证据；覆盖实际受支持的执行方式，无法运行的验证明确承接，不将示例发布当成第一方生产者已完成切换。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。

### 2026-09-11 实施接口与后续迁移交接

固定基线 `6976e10`，工作分支 `codex/observation-ticket-02`，工作区
`/Users/bytedance/.codex/worktrees/705c/Heartbeat`。按最新用户指令，仅保留工作区改动，不创建提交；
父 PRD、ORCHESTRATION 及其他 issue 由协调任务维护。

- `CollectorFact` / `BoundCollectorFact` / `FactSubmission` 末尾增加可选 `Kind`、`Source`；
  `Kind != null` 明确选择原生独立事实，支持 segment/event，FactId 接受稳定非空 UUID，
  不再把 FactId 限制为协议消息的 UUIDv7。消息及 Gap 身份仍沿用原有规则。
- `CollectorId` 是具体 Observer 的稳定身份，不必等于 Runtime InstanceId。Runtime journal
  新增本地 `DeliveryInstanceId` 保管归属；它不进入上传，也不决定 Id、FOI 或 Kind。
  同一 Instance 下两个 External Hosts 可保管各自独立 Observer 的事实。
- SDK 的 `RequiredSubjectKind: null`、`Outputs: []`、`BindingId: ""` 允许无交付分组发布。
  Hub 提供 `CreateInstance(package, spec, instanceKey)` 和 `activation.PublishAsync(messageId, facts)`；
  `CollectorInstance.Subject` 的 default / 空 SubjectId 明确表示缺省，wire 编码 `subject: null`。
  `StreamId: Guid.Empty` 只是缺省，SDK stdio 省略该字段；Runtime/HTTP 不创建虚构 Stream/Subject 行。
- 最低能力为 `facts.observation: 2`，它覆盖必需 Aspect，不另要求旧 `facts.aspect`。
  SDK、InProcess、ManagedProcess、ExternalHost 入口均明确区分 Kind 原生输入；
  新输入不允许借旧 ObserverId/Target 或旧缓存 envelope 补齐身份。
- `FactUploadItem` 的原生项使用 `Observation`，旧 `Stream/Fact/Gap` 为空；`IsFinal` 和 `ObservedAt`
  留在本地快照中。`HeartbeatApiClient.UploadFactsAsync(batch)` 将原生项写入 `/api/v1/observations`，
  历史项和实际 Gap 仍经 `/api/v1/facts`。混合批次只有全部成功才确认，部分成功重试保持幂等。
- Runtime schema 9、含原生事实的 SDK outbox/dead-letter schema 4；已确认终态保留在有界重放窗口，
  进行中/未确认事实不能被容量回收。迟到 ACK 核对版本、Kind、Collector、FOI、Aspect、Source、
  Result、Relations、家族时间和本地终态/ObservedAt；不确认不同内容或更高版本。
- Runtime/SDK/服务端复用 `ObservationValidation` 验证完整原生形状；服务端仍负责实际对象解析、
  Person 已存在及 Owner 约束。关系和成员顺序不影响同版本语义比较，成员对象变化仍冲突。
- 无 Binding 的 SDK 满容量施加背压，保留原 pending，不伪造 Gap；有实际 Binding 的丢失仍由已有
  原子 outbox mutation 保管准确 GapId、丢失范围及数量，重启重放不改变其身份。

03 / 04–06 的承接要求：

1. 03 继续完整旧缓存/数据库身份接管矩阵；本票保留 Kind=null 旧输入适配及 schema 1–8 Runtime、
   schema 1–3 SDK 读取，不将历史 FactId 直接冒充新的全局原生 Id。旧 pending 排空、旧引用/版本保全、
   已安装版本盘点和离线/回退窗口仍须按 03 规格验收。
2. 04–06 为实际生产者设置显式 Kind、真实稳定 CollectorId、FOI、Aspect、Result 和家族时间；
   保留已有 Observer（尤其 Browser 安装 UUID），不要换成 Runtime InstanceId。每条事实保持单调 Revision，
   IsFinal 的终态/精确 ACK 责任保留。包能力应宣告原生 v2，仍需读取旧状态的包保留实际所需兼容版本。
3. `Payload` 名称剩余位置：SDK `CollectorFact/BoundCollectorFact`、Hub `FactSubmission`、协议 wire `payload`、
   Runtime journal `CommittedFactState.Payload`；它们均原样承载完整 Result，只是一份内容。
   HTTP 独立入口为 `ObservationSnapshot.Result`，原始读取的 Payload 为 01 保留兼容别名。
   旧 `FactSnapshot.Payload`、活动/input 投影与旧 HTTP 入口仅服务历史及尚未切换生产者；
   09 结合 03–06 的实际消费者/缓存盘点决定退出，不要求永久双写。
4. 本票通用发布 fixture 不代表 System/Browser/VRChat 已切换；真实生产者和专有状态迁移由 04–06 验收，
   07/08 及父 PRD 整体状态由各票和协调任务负责。不部署、不推送、不操作业务库或暂停的生产副本。

### 2026-09-11 失败→通过及审查证据

- Runtime 最小公开协议测试先观察到不完整显式原生 Fact 被旧语义推断并 ACK（Assert.False 失败）；
  修复显式分流后拒绝。真实 HTTP fixture 同时覆盖缺 Collector/FOI/Aspect、空 Result、无效对象 scope 和关系，
  拒绝后无 pending；有效 Segment/Event 才能进入持久保管。
- 无 Subject/Stream 原生初始化先失败 `outputs must be a non-empty array`；扩展 manifest/初始化后三执行方式
  均成功，缓存重启及 HTTP 读取保持无 Stream。SDK 无 Subject 初始化先抛 ArgumentNullException，修复后
  离线→重启→发布→ACK→再次重启通过。
- SDK 同版本冲突原被静默忽略；PendingFacts 原泄露可变 Relations 引用；关系逆序原误报冲突。
  对应公开行为最小测试先失败，修复后完整内容拒绝/保护/幂等通过。仅 obs2 协商原无法 drain，修复后通过。
- SDK/ExternalHost 显式原生输入曾被旧 ObserverId/Target 补齐，ExternalHost 先返回错误的 200；
  修复后明确 400 并无 pending，SDK 旧 envelope 原文件保留。旧包读取 schema 4 被提前阻止，文件不改写。
- HTTP Segment/Event 理论测试贯通公开协议、真实 Runtime journal 重启、实际 HeartbeatApiClient、
  WebApplicationFactory HTTP、隔离 PostgreSQL 与原始 GET。读取保留生产者 UUID、未知 Result、Aspect、空 Relations；
  上传途中更高版本仍待发，同版本 Source 或本地 IsFinal 被改动无法确认，最终准确 ACK 后重启无待发。
- Standards 审查发现原生 CollectorId 错绑 InstanceId：上述 HTTP 两项先 Rejected；改为独立 Observer +
  DeliveryInstanceId 后 2/2 通过。ExternalHost 另覆盖同一 Instance 两 Host/Observer 的 UUIDv4 事实，
  重启保留原身份，逐项 ACK 完成前始终阻止删除 Instance。
- 两轴审查发现 stdio 仍用旧 UUIDv7 FactId 限制：实际 ManagedProcess 发布 UUIDv4 先超时失败，
  原生改用非空 UUID 校验后 1/1 通过，重启和 ACK 保留该固定 UUID。
- 全仓首次回归暴露旧测试把缓存版本 8 写死，导致旧字段转换 fixture 没真正回到 v1；更新 fixture 到当前 9
  后包括该测试的 13 项回归通过。没有放宽实际旧缓存读取或篡改历史迁移。

双轴 code-review：固定点 `6976e10`，Standards 两项代码发现及 Observer 文档尾项、Spec UUID 发现及文档尾项
均已修复并独立复核关闭；两轴当前无未关闭项。审查后遵守最新不提交指令，最终内容以工作区 diff 为准。
兼容对象、版本、退出门槛及验证方式同步到 `docs/architecture/compatibility-debt.md`、SDK/Hub/contracts README。


### 2026-09-11 最终验证与 lifecycle / friction closeout

- `dotnet tool restore`、`dotnet restore Heartbeat.slnx`：成功。新 worktree 初次 `--no-restore` 缺 assets，
  经正常 restore 恢复；没有修改工具版本或跳过依赖检查。
- `dotnet build Heartbeat.slnx --no-restore`：最终成功，0 errors；已有 nullable/analyzer warning 未作为成功测试替代。
- `dotnet test Heartbeat.slnx --no-build --nologo`：最终 13 个项目 **1,385/1,385 通过，0 失败，0 跳过**。
  该最终全仓运行在全部代码修复之后执行，包含服务端隔离 PostgreSQL 622 项、Hub 322 项与 SDK 51 项。
- 受影响范围先行复验：`dotnet test collection/hub/Heartbeat.Collection.Hub.Tests --no-build --nologo`
  322/322；`dotnet test collection/protocol/Heartbeat.Collection.CollectorProtocol.Tests --no-build --nologo`
  51/51；服务端 filter `FullyQualifiedName~FactHttpTests|FullyQualifiedName~FactStoreTests|FullyQualifiedName~ObservationStorageTests`
  100/100。它们与全仓运行重叠，不相加计数。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 与
  `git diff HEAD --check`：通过。
- 本机可复核日志：`/tmp/heartbeat02-all-final.log`、`/tmp/heartbeat02-build-final.log`、
  `/tmp/heartbeat02-style-final.log`；最小失败日志包括 `heartbeat02-red.log`、`heartbeat02-observer-red.log`、
  `heartbeat02-uuid-red.log`，对应 green 与受影响范围日志保留在相同目录。
- 文档漂移已经修复：contracts/Hub README 不再混淆 Observer 与运行 Instance；旧协议/缓存兼容记录了
  实际消费者、退出条件与验证方式。仅维护本 issue；父 PRD/其他票的状态不在本 worktree 更新。
- 本项实现、自动验证、Standards/Spec 双轴审查及验收均完成，无本票剩余人工门禁。
  未执行部署、业务库变更、推送、真实安装或暂停的生产副本演练；这些仍由已有外部发布任务承接。
  03–06 的完整历史/实际生产者验收没有被本票提前声明完成。
- 根据最新用户指令，HEAD 保持 `6976e10093c3d91623e48df617df5016fb4bcb70`，全部实现/测试/文档为工作区改动。
  不留新的实现 commit；协调任务按文件差异整合。
