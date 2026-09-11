# 04 — System 活动与输入全链路切换

**What to build:** 真实 System Collector 的桌面活动和输入事件使用独立观测契约，从生产、持久保管、上传到读取完整运行，升级及重启不会丢失进行中活动或待发输入。

**Blocked by:** [03 — 旧事实与旧缓存无损接管](03-legacy-fact-and-cache-takeover.md).

Status: ready-for-human

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [x] 真实 System 活动与输入生产者经 SDK/Runtime/HTTP 写入 PostgreSQL 并读取；使用新事实自身的 Id、Kind 和完整观测信息，旧 Subject/Stream 仅在仍需管理/兼容的职责中存在。
- [x] System FOI 保持机器，具体 Observer 身份跨重启稳定；desktop-activity 与 input 语义明确，App 关系只来自实际观测，无 App 的桌面状态也完整保存。
- [x] 同一活动增长或收尾保留 Fact Id，变化快照递增 Revision；实际转场产生新事实，固定身份属性与家族时间规则一致，既有标题/away/恢复及区间轮换行为保持。
- [x] 输入 Event 的发生时刻、Id 和完整编码结果保留；重传不重复存储，未变化的事件可保持 Revision 1。
- [x] 活跃 checkpoint、旧活动/input 缓存及待发记录经真实升级/恢复入口接管，保留原身份和版本；恢复不延伸没有观测依据的历史区间。
- [x] 离线、重启、持续活动期间的迟到 ACK 及 Gap 保管通过完整链路验证，旧确认不能清除当前新快照。
- [x] Windows/macOS 的受支持 System 路径均纳入切换与依赖盘点；可运行的平台测试通过，不可运行部分提供可重复步骤和明确承接者。
- [x] 本任务的读回验证使用实际观测结果与关系；全量分析/Dashboard 行为由 08 承接，不能靠仅构造 HTTP 数据声明 System 已切换。
- [x] 对应 System、协议/Runtime 和 HTTP 集成回归通过，记录自动与实际安装验证的区别；仍需人工门禁时按仓库规则保留待验收状态。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。


### 2026-09-11 实现与隔离验证

基线 `e6564fc296f83df4fa9d32cb91560f3f954cf797`，分支 `codex/observation-ticket-04`，
工作树 `/Users/bytedance/.codex/worktrees/e487/Heartbeat`。未使用旧临时补丁。
实现与自动验收完成后仍保留 `ready-for-human`；下面的实际安装/权限与窗口盘点尚未完成。

- 真实 AppMonitorService / InputEventBuffer → 公共 Collector SDK → System InProcess → Runtime →
  `/observations` → 隔离 PostgreSQL → 公开 Fact 查询，保存生产者 UUID、原生 Kind 与完整 Result。
  System Package 1.2.0 声明 `facts.observation:2`；生产 Fact 使用空 BindingId。
- Observer 为稳定 System Instance UUID，FOI 是实际机器。明确平台 App 关系随活动保存；无 App
  桌面和 away 继续保存完整机器活动，但不创建虚构 App 关系。旧 away 快照沿原兼容关系保管。
- 同活动增长/缩短/收尾递增 Revision 且 Id 不变，转场与轮转新建 Id。InputEventBuffer 的键位码、
  CodeSet、按钮/滚轮与微秒时刻完整；事件重传仍可保持 Revision 1。
- ingress 每次 mutation 写 schema2，包括排空 reset；新记录 `IsObservation=true`，旧 NDJSON
  checkpoint/input 默认 false，沿原 Binding/Stream/FactId 与 Kind=null 正常结束和准确 ACK。
  checkpoint 在原 End 以 Revision+1 收尾，停机区间只形成 Gap，不扩张历史。
- 私有格式实际为 AppMonitor + NDJSON checkpoint、通用 SDK outbox 与旧 Segment/Input JSON；
  当前 System 不依赖单独的 .NET Segment SDK 状态文件。未改共享 SDK 格式或06 ManagedProcess门禁。
- 共享 Runtime live Source stamp 曾只查 Stream，System 原生输出后现存 transcript 稳定失败。
  修复为逐条已 ACK native Fact.Source；拒绝项、null Source、磁盘恢复与 Analytics Confirm 不盖戳。
  既有实时 duplicate/superseded/same-message retry 活跃语义保持，经协调确认。

#### 自动证据

- RED：无 App 桌面没有事实 1 FAIL（`/tmp/hb04-desktop-red.log`）→ 1 PASS；真实 win/mac
  producer 原生 Observation 为空 2 FAIL（`/tmp/heartbeat04-http-red.log`）→ 通过；input native 标记、
  reset版本保护、native Source stamp 均先确认行为失败后修复（`heartbeat04-ingress-*`、`hb04-source-*`）。
- 全仓 .NET 初跑1437 PASS/2 FAIL（`/tmp/hb04-all.log`），两项旧System HTTP fixture已修正：
  长会话在历史25小时运行以遵守SDK真实时钟边界，混合原生input改用真实混合上传API。System HTTP
  7/7复验通过（`/tmp/hb04-server-regression.log`），最终1439个不同测试均有通过证据；未声称初跑全绿。
  含System94、Windows38、macOS82、Hub333、SDK59。最终build（0 warning/0 error）、IDE1006与diff检查通过。
- 真实 System HTTP 5/5（`/tmp/heartbeat04-http-capacity.log`）：win/mac活动增长/缩短/收尾、
  MaxDurableFacts=1 下已 ACK 活动仍保留、迟到 ACK、离线/重启、无 App/away、实际输入编码和容量 Gap、
  `/observations`成功而`/facts`失败时整批不误确认、原始旧 first-stage checkpoint/input 接管与唯一读回。
- ingress/兼容20/20、真正子进程 crash+两次独立重启1/1、旧 Segment/Input JSON与保管回归8/8。
  日志 `/tmp/heartbeat04-ingress-all.log`、
  `/tmp/heartbeat04-crash-green.log`、`/tmp/heartbeat04-legacy-private-cache.log`。
- `python3 scripts/verify-system-ingress-rollback.py` 通过：编译 e6564fc 未修改的实际旧 loader，
  对 native input、已 ACK checkpoint、排空 reset 拒绝读取且文件集合/SHA256不变；新版再次恢复成功。
  证据 `/tmp/heartbeat04-ingress-rollback.log`。不把反序列化失败本身当作完整回退证明。
- Frontend verify：类型检查、308测试与构建通过；Browser 111/113，2条host.integration失败在精确
  e6564fc独立git archive同样复现（`/tmp/heartbeat04-browser-baseline.log`）。根因为旧TestHost只读/清
  sink投影，真实Runtime仍保管事实；05已承接TestHost/fixture修复，需协调合并后重跑，不能算通过。
  Browser构建及`node scripts/collector-contracts.mjs check`通过。

可复验入口：

```sh
dotnet test Heartbeat.slnx --no-restore
dotnet test server/Heartbeat.Server.Tests --no-restore --filter FullyQualifiedName~SystemObservations_
python3 scripts/verify-system-ingress-rollback.py
npm --prefix frontend run verify
npm --prefix collection/collectors/Heartbeat.Collector.Browser test
npm --prefix collection/collectors/Heartbeat.Collector.Browser run build
node scripts/collector-contracts.mjs check
```

#### 剩余人工门禁与09交接

- [ ] owner 在 Windows 和 macOS 分别验收真实原生窗口/标题/away/恢复及 input权限、回调、实际安装；
  具体步骤和平台生产依赖见 [System README](../../../collection/desktop/Heartbeat.Collector.System/README.md)。
  可控平台 observation fixture 与适配器单测不替代实际安装。
- [ ] owner 提供所有实际 Desktop/System Package版本与contentHash、Profile/schema、未发/未终结/
  隔离数量及最老待发时间，批准最长离线和回退窗口；现场清单未获得，不能删除兼容。
- [ ] 协调任务合并05后复验Browser已确认的两项基线失败；本票未改该目录或将失败报为通过。
- 09需同步System Package1.2.0/capability2、私有ingress schema2以及System原生事实路径；
  通用SDK仍schema4、Runtime仍schema9。父PRD/ORCHESTRATION及其他issue由协调者维护。
- 历史完整副本的数据smoke、业务库迁移、资源/恢复与发布继续由原observation-storage门禁承接；
  未推送、部署、操作业务库、真实账号或恢复暂停演练。本票独立PG测试不冒充上述门禁。
- Friction closeout：修复System README/InputEventBuffer仍声称新事实回投旧buffer的漂移；记录所有
  私有兼容消费者和退出标准；Source状态真实回归已修。平台/安装/窗口缺口有owner和步骤，保持待验收。


## Standards

独立审查固定 `e6564fc…e5a0439` 的非空20文件实现差异。无文档规范违反或需修复的维护性发现。
兼容消费者、退出条件、可重复验证与owner人工门禁均已记录，状态保持ready-for-human。
审查为只读，未重跑测试；最终补充仅为本审查证据。

## Spec

独立审查固定 `e6564fc…e5a0439`，无缺失/错误实现或scope creep发现。已追踪真实生产者→SDK/
InProcess→Runtime→HTTP/查询、旧身份接管、checkpoint收尾/Gap、schema2排空回退保护，
并核对上述HTTP/持久化/旧loader日志。Windows/macOS原生与实际安装门禁、Browser基线复验均未冒充完成。

两轴合计：Standards 0；Spec 0；均无未解决最高风险项。代码/必要文档/本票issue证据保留在同一
Conventional Commit，未产生纯进度提交，父PRD与其他issue未修改。
