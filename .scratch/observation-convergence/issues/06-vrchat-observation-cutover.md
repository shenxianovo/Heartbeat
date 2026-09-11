# 06 — VRChat 账号观测全链路切换

**What to build:** 真实 VRChat 账号位置观测使用独立事实契约，经托管 Collector、SDK/Runtime 和 HTTP 保存并读取；checkpoint、离线恢复和旧事实收尾保留身份，没有设备依据也能工作。

**Blocked by:** [03 — 旧事实与旧缓存无损接管](03-legacy-fact-and-cache-takeover.md).

Status: ready-for-human

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [x] 真实 VRChat presence 状态机与托管发布路径使用新契约，贯通 SDK/outbox、Runtime、HTTP、PostgreSQL 与查询；不以旧账号 Subject/Stream 作为新 Fact 存在前提。
- [x] FOI 为明确账号，Observer 跨正常重启稳定，Aspect 保持 account-location；没有运行设备或 App 证据时关系为空，不用采集宿主或当前登录补历史。
- [x] 账号位置持续观测保持 Fact Id 并递增 Revision，位置转场及实际身份变化产生相应新事实；增长、轮换及收尾遵守固定属性和家族时间规则。
- [x] 从 active checkpoint 恢复后正确收尾；即使 End 不变而终态变化，发布版本也可更新。不得将停机期间无观测依据的时间扩充为已有事实。
- [x] 所有仍支持的 VRChat checkpoint、pending Facts/Gaps 和专有持久状态经真实迁移入口恢复，保留 Id、版本、内容、时间及确认状态。
- [x] 旧未知账号事实与升级后的明确账号按已确认映射规则区分，旧事实正常结束和重放不被当前账号接管或静默合并。
- [x] 离线、进程重启、迟到 ACK、重复/乱序和 Gap 通过实际托管链路验证，服务端读回保持准确账号归属和唯一事实。
- [x] ManagedProcess 协商和旧包回退不会读取不支持的新状态；失败保留可恢复资料，账号授权和运行管理职责保持。
- [ ] 对应 presence/checkpoint、托管协议/Runtime、HTTP 回归通过，记录真实账号安装验收步骤及证据；缺少现场验证时明确承接状态。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。


### 2026-09-11 实施与自动验收

基线 `e6564fc296f83df4fa9d32cb91560f3f954cf797`，独立工作区
`/Users/bytedance/.codex/worktrees/7292/Heartbeat`，分支 `codex/observation-ticket-06`。
开始时 main/HEAD 与基线相等且工作区干净；没有使用旧补丁。只维护本票与必要实现/测试/说明，
父 PRD、ORCHESTRATION、全局术语/旧版本叙述由协调任务及09维护。

- 真实 presence publisher 为新 Fact 显式记录原生标记与持久 CollectorId，公开状态机/ManagedProcess
  用稳定 Instance Observer；Fact 保持明确账号 FOI、account-location、空 Relations，Kind=segment、
  Source=vrchat.account、无 Binding 的原生发布。持续/轮换/转场/同 End 收尾保持 Revision 规则。
- 专有 checkpoint v1/v2/v3 literal fixtures 来自历史 Git 定义，保留旧 Fact/Gap Id、Revision、时间、
  Active 与 pending。升级先验证、保留原 `.vN.bak` 再原子写 schema4；旧事实保持 Kind=null 与 presence
  Binding/原 Stream 映射，未知账号保持 null。进行中恢复以原 End 收尾且另发 process_restart Gap。
- SDK schema4 与 Runtime schema9 的保管/精确ACK沿已验收03实现；本票没有改共享Segment SDK或协议。
  程序/Package 协商 facts.observation:2。专有状态先写通用 collector-data-requirements.json，
  Runtime 手动启动、候选更新及 LastKnownGood 回退均校验，SDK排空也不允许旧包读取schema4。
  未知/坏要求或IO失败阻断并保全，兼容包恢复可继续。Hub不识别VRChat文件或业务格式。
- 第一个公开producer测试稳定RED为 Kind预期segment/实际null；实际apphost托管测试独立重复RED为
  Observation为空。checkpoint历史迁移4 FAIL、未知schema1 FAIL、marker4 FAIL、native身份4 FAIL
  分别修复后通过；通用启动/回退5 FAIL→5 PASS，要求路径损坏为目录另取1 FAIL→GREEN。
- 实际ManagedProcess fixture构造当前平台Package并经真实stdio SDK/outbox与Runtime保管；两个mock
  账号分别授权、修订、离线、杀掉仅本测试子进程、Runtime重启及checkpoint收尾，经HeartbeatApiClient
  到隔离PostgreSQL和公开GET读回。覆盖同Id最终Revision/End不扩张、两账号/Observer隔离、真实Gap、
  混合HTTP部分成功503重试、重复/乱序、迟到ACK及同版错误IsFinal不确认。未访问真实VRChat账号。

### 兼容退出、09交接与现场承接

专有格式、真实fixture入口与可重复安装验收见
[VRChat README](../../../collection/collectors/Heartbeat.Collector.VRChat/README.md)；
通用marker/启动回退要求见[缓存兼容](../../../docs/architecture/observation-cache-compatibility.md)。
仍持有v1–v3 Active/pending、SDK outbox/dead-letter、Runtime delivered/未终结条目或LastKnownGood的
目录都是实际兼容消费者；格式升级本身不是移除条件。现场完整安装Package/contentHash、最长离线和
回退窗口由owner与Headless发布维护者在原observation-storage发布门禁承接，本票保留所有兼容。

当前平台自动fixture与真实账号安装证据分开：尚无真实账号安装记录，也未运行linux-x64现场安装。
README提供逐账号清单、目录备份、安装/授权、离线上行、重启Gap、GET核对及隔离目录回退步骤，
由owner / Headless发布维护者执行并把证据附本票。此人工缺口使状态保持ready-for-human，最后一项
验收不提前勾选；不以测试总数替代现场验收。未推送、部署、操作业务库或恢复暂停的完整副本/资源演练。
09需统一核对当前VRChat原生生产、checkpoint4及保留presence流仅用于历史和真实Gap的叙述。

### Lifecycle / friction closeout

已修复实际旧生产路径和仅私有状态绕过旧包门禁，更新受影响测试的HTTP路由，不再把当前Runtime JSON
重标历史schema作为历史fixture；03通用历史矩阵与本票真实专有格式矩阵分别承接。格式失败保全、重试
和退出门槛有可判定自动入口，人工安装门禁有明确步骤/承接者。全局模型文档漂移交09，不广泛重复修改。

固定审查基线 `e6564fc`，初审索引树 `e78fe5e2dfb0b7a8ac4b1ab3b0aa764a8523ba85`，
最终代码树 `1c7edf750a08816c0af3b9be30887b79da294c3e`；以包含全部新文件的固定树审查，
没有为进度或审查创建分支提交。此树之后仅追加本票完成证据。

- Standards 初审发现重复要求属性经 Dictionary 吞掉，导致冲突原件被改写。新增重复信封、重复能力键、
  空要求集合失败回归，写端改为与Runtime一致的严格解析，拒绝并保全。
- Spec 初审发现坏JSON原件先移走、后续要求/新checkpoint提交失败，会使下次正常Open漏恢复Gap。
  新增损坏JSON+提交IO失败→移除故障→重试的RED；保留源路径并复制隔离证据直到恢复状态原子替换成功。
  恢复Gap范围、原字节和重载后Id验证通过。
- 上述审查回归 **4 FAIL→GREEN**，日志 `/tmp/heartbeat06-review-red.log`；修复后VRChat全套
  **49/49 PASS**，日志 `/tmp/heartbeat06-review-vrchat-green.log`。两位独立审查者复核关闭各自P2。
- 真实Native/legacy v1–3 ManagedProcess→HTTP矩阵 **5/5 PASS**，`/tmp/vrchat-http-matrix.log`；
  通用启动/回退门禁 **6/6 PASS**，`/tmp/heartbeat06-gate-final.log`。均与全仓验证重叠，不累计计数。

## Standards

固定最终树复核通过。要求文件写端与Runtime读端一致拒绝重复属性、空集合和冲突，保留原件；
恢复提交失败可重试。初审1项P2已关闭，最终0未解决硬违反，0可行动smell。

## Spec

固定最终树复核通过。损坏checkpoint恢复Gap的提交失败窗口以RED→GREEN关闭；无未解决规格缺失
或scope creep。真实账号安装与兼容退出窗口保持明确人工承接、ready-for-human。

两轴合计：Standards 1项已关闭、0未解决；Spec 1项已关闭、0未解决；两轴均无剩余最高风险项。


### 最终冻结代码验证

- 环境为 macOS / arm64，实际VRChat apphost构包、ManagedProcess运行fixture和隔离PostgreSQL通过；
  这不替代linux-x64发布安装或真实账号现场验收。
- `dotnet tool restore`、`dotnet restore Heartbeat.slnx` 成功；基线和最终
  `dotnet build Heartbeat.slnx --no-restore` 成功，最终0 warnings / 0 errors。
- `dotnet test Heartbeat.slnx --no-build --nologo` 最终 **13项目、1,459/1,459 PASS，0失败、0跳过**；
  含真实隔离PostgreSQL服务端651、Hub338、VRChat49、SDK59。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`、
  `git diff --check` 与 staged diff检查通过。未改前端、Browser TypeScript或部署脚本。
- 最终日志 `/tmp/heartbeat06-build-reviewed.log`、`/tmp/heartbeat06-all-reviewed.log`、
  `/tmp/heartbeat06-style-reviewed.log`。先前 `/tmp/heartbeat06-all-final.log` 是测试迁移编辑期间
  的中间运行，4项旧测试断言失败已修正，不能用其替代本次冻结代码的最终结果。
- 双轴审查通过后仅追加本票验证证据；正常创建单一Conventional Commit，未推送。实现与自动验收
  完成，真实账号/安装门禁未完成，最终状态仍为ready-for-human。
