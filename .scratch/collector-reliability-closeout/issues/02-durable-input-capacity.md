# 02 — durable InputEvent 满容量不再静默丢失

Status: needs-triage

Owner: Collection / System Input + Protocol

Priority: P1 — 当前 durable cache `Replace` 会 trim oldest，却没有对应 Gap，ACK 连续性被伪造。

## What to build

为 InputEvent durable projection 的容量耗尽建立明确协议结果。首选让 protocol/outbox 的 durable
backpressure 成为唯一容量边界；若必须 evict，则在同一原子 mutation 中持久化覆盖精确序列范围的
Stream Gap。任何路径都不能只调用 `JsonFileCache.TrimToCapacity` 后继续宣称连续。

## Acceptance

- [x] 指定唯一容量 owner；`InputEventBuffer`、protocol outbox 与 `JsonFileCache` 不再各自静默裁剪同一
  事实窗口。
- [x] 满容量时 producer 得到可判定 backpressure，或 durable state 同时记录精确 Stream Gap；崩溃点
  不会出现“事件已删但 Gap 未写”或反向不一致。
- [x] drain/ACK 只删除已确认 event；retry/restart 保持 event/gap 序列和 FactId，不重复吞吐。
- [x] Current/diagnostic 能区分 backlog、backpressure、gap 与普通 idle，不把 count clamp 当作成功。
- [x] 故障注入测试覆盖 capacity-1/capacity/capacity+1、并发 enqueue+drain、cache replace crash、restart、
  ACK/retry 与 gap upload。
- [x] 现有 `DropsOldest` 测试被替换为协议真实性断言；所有 System/Hub/Protocol tests 通过。
- [x] 对当前真实 cache version 的迁移有 backup/atomic rewrite/recovery 测试；无现场证据的旧格式不猜测。

## Comments

### 2026-08-30 — TDD 实现与 friction closeout 完成

`InputEventBuffer` 现在是 Hub committed Event → Analytics 上传之间 durable projection 的唯一容量
owner。投影满容量会抛出可判定 backpressure，Runtime 返回 Retry，Collector Protocol outbox 保留同一
FactId；只有 confirmed drain 会删除已 ACK event。所有 InputEvent `JsonFileCache` 实例改为只做原子
持久化、以 `int.MaxValue` 保留当前 v2 内容，不再在投影层之外 trim。原先 `DropsOldest` 断言已替换为
capacity-1/capacity/capacity+1、over-capacity restart、ACK/retry 与并发 drain/enqueue 的真实性测试。

平台 hook 不能阻塞，因此 System 原始 ingress 的 emergency overflow 在返回前写入独立 durable Gap
ledger；claim/in-flight/ACK 都是原子 mutation，崩溃重放保持稳定 GapId。Collector outbox 自身容量淘汰
也在同一 JSON mutation 中写 Gap，Event 与零时长 Segment 均转换为可被 Hub 接受的最小 1 tick 半开
区间。诊断状态新增 `Backlog`、`Backpressure`、`GapRecorded`，Gap ACK 后无 backlog 会明确回到
`Ready`。

独立 review 发现 Hub 过去只按 stream/range/reason 去重，会吞掉同一 instant 的独立 loss。friction
closeout 将稳定 UUIDv7 `GapId` 贯穿 InProcess、stdio/ManagedProcess、Browser HTTP 与 Hub durable
state；相同 GapId/不同内容明确 conflict，不同 GapId 即使范围相同也分别提交。当前 Browser durable
记录会在 `loadDurable` 返回前原子补 GapId；当前 Hub runtime state v2 中无 GapId 的 committed Gap
只在精确匹配 lost-ACK retry 时原子绑定该 ID 并返回 Duplicate。两处兼容只覆盖当前真实格式，代码已
记录移除门槛。当前 Collector outbox schema v1 曾生成的零长度 Event eviction Gap 也会原子改写为
1 tick 半开区间而不 quarantine 仍保留的 Facts；未猜测更旧 schema。

验证：System 53/53、Hub 208/208、Collector Protocol 8/8、Mac 78/78、Windows 47/47、VRChat
13/13、Reference ManagedProcess 1/1、Headless 6/6、Desktop UI 25/25、Browser 77/77 且 production
build 成功；`dotnet build Heartbeat.slnx --no-restore --configuration Debug` 为 0 warning / 0 error；
`git diff --check` 通过。现有 `JsonFileCache`/`HeartbeatCacheFormats` 测试继续证明 current InputEvent
v1→v2 backup、原子 rewrite、失败不替换与 restart recovery。独立复核确认原两项 P1/P2 finding 已
关闭；随后发现的 current-state lost-ACK 迁移边角也以失败测试关闭。

### 2026-08-30 — closeout 复审重开

`SystemCollectorProtocolAdapter.Publish` 先 `TryEnqueue`、失败后才单独 `RecordDrop`，两次 mutation
之间崩溃会留下“Event 已拒绝但无 Stream Gap”。此外 ingress store 的同步 append+fsync 位于平台
UI/window/input 回调路径，违反 ADR-040 与 Collection glossary 的 Collector Ingress Queue interface。
原子持久化语义必须移到不阻塞平台回调的后台边界；关闭前本 issue 恢复为 `needs-triage`。

## Reopened acceptance

- [x] Event 接受或拒绝与对应 Stream Gap 在一个可证明的 durable atomic mutation 中提交。
- [x] 平台 UI、window 与 input 回调只做内存入队，不同步等待磁盘 fsync。
- [x] 盘点并统一本 feature 触达的 atomic JSON replacement，或记录明确暂缓原因与退出条件。

### 2026-08-30 — 责任边界裁决回写

上一轮“平台 hook 不能阻塞，因此 overflow 在返回前写 durable Gap ledger”的 owner 评论与 ADR-040、
Collection glossary 冲突：同步写 ledger 仍会让原生回调承担磁盘延迟。ADR-040 现明确回调返回只代表
进入易失 ingress queue；后台 pump 才拥有 durable capacity 判定，并在同一 journal mutation 中提交
Event 或覆盖它的 Stream Gap。进程在 pump 前终止属于明确的 volatile window，不得误报为 durable；
实现将删除 callback `TryEnqueue -> RecordDrop` 的双存储路径。

### 2026-08-30 — atomic ingress mutation 实现

先以编译失败测试固定 `StageInputEvent` seam，并用真实受阻文件路径证明原生 InputEvent callback 不能
触碰 journal。`SystemCollectorIngressStore` 现在在同一锁、同一次 append+fsync 中根据 durable Event
容量写入 Event 或稳定 UUIDv7 的单 tick Stream Gap；restart 从同一 NDJSON 恢复二者。adapter 的
segment/input callbacks 一律只写内存 channel，后台 pump 才 stage、上报与 ACK。独立
`SystemInputIngressGapStore` 已删除，故不存在“TryEnqueue 返回 false 后进程崩溃、Gap 尚未写”的第二
存储窗口；没有为无现场证据的该临时 JSON 格式增加迁移分支。

验证：System 55/55、Windows 47/47、Mac 78/78；atomic stage、受阻 journal callback、overflow
protocol 三测试重复 10 次 30/30；`git diff --check` 通过。atomic JSON replacement 全仓盘点仍待 P2
closeout，因此 issue 保持 `needs-triage`。

### 2026-08-30 — atomic replacement audit and bounded deferral

本 feature 实际触达的 replacement 已逐项盘点；它们当前不是可机械合并的同一契约：

| Store | 当前保证 | 关键差异 |
| --- | --- | --- |
| `CollectorProtocolOutbox` | 同目录唯一 temp 后 overwrite；delivery outcome 的最终 replacement 与 deadline fence 线性化；finally 清理 temp | 无 fsync、无 replacement 验证；所在 Protocol 项目不依赖 `Heartbeat.Core` |
| `SystemCollectorIngressStore` | 同目录唯一 temp、WriteThrough + fsync、严格 temp 回收 | 32 KiB target 的 history-bounded COW NDJSON chunks；不可拆 oversized 原子记录独占 chunk；ACK tombstone/reset 与 tail repair 共同定义 journal 语义 |
| `VRChatPresenceCheckpoint` | 同目录唯一 temp、finally 清理 | JSON envelope，无 fsync；Stage 的内存回滚依赖 Persist 抛错边界 |
| `JsonFileCache` / `JsonDeadLetterStore` | 固定 temp、替换前反序列化验证、finally 清理 | cache 还拥有 migration backup / unavailable 状态；当前无 fsync |
| `JsonCollectorRuntimeStore` | 固定 temp、WriteThrough + fsync、替换前验证、finally 清理 | 对持久化异常统一包装为 runtime state failure |

本轮不抽取公共 helper：最低层 `Heartbeat.Collection.CollectorProtocol` 当前是独立协议程序集；把公共
实现放进 `Heartbeat.Core` 会新增 protocol → domain/shared 依赖，放进现有 Hub/System 项目则形成反向
依赖，而新增 solution-wide storage project 并同时修改五种失败契约会超出此可靠性收口的风险边界。
只迁移一部分也会留下两个“公共”实现，不能消除权威漂移。这里没有证据允许把无 fsync 路径悄然宣称
为 durable，或把 journal/迁移验证简化成普通 JSON replace。

暂缓退出条件：owner 先裁定一个不依赖领域或 Protocol 的低层 storage primitive 归属，并固定
同目录唯一 temp、write-through/fsync、替换前可注入验证、失败清理、异常契约、锁责任及 Windows /
macOS / Linux directory-entry durability 的明确 contract；随后为上表每个 store 建立 replacement 前、
写一半、验证失败、replace 前后 crash 的 characterization/restart tests，再按 store 独立迁移提交。
完成标准是生产路径不再自行调用 temp + overwrite，且三平台测试证明原有 migration backup、outbox
重放、checkpoint 内存回滚和 ingress tail repair 语义均未改变。退出条件满足前，本审计关闭 P2，公共
实现本身明确暂缓，不把“看起来原子”误报成跨崩溃 durable。

### 2026-08-30 — valid NDJSON tail termination repair

复审补充了“最后一条 JSON 完整但缺少 LF”的 crash fixture：旧 `Open` 会接受该条，下次 append
把两个 JSON object 粘成一行，再启后丢失新条目。新测试先稳定失败（期望两个 FactId，实际仅
恢复一个）；`Open` 现在只在最后一行通过严格反序列化与 entry 校验后，以 WriteThrough +
fsync 补齐 LF，然后才允许后续 append。验证：ingress store 7/7；System suite 62/62。

### 2026-08-30 — InputEvent / Gap journal order 保真

Spec 复审指出 store 将 accepted Event 与 overflow Gap 恢复到两张 list，adapter 又固定先交付
Gap；任意 ACK rewrite 也会把两类重排，因此 restart 后 Gap 可越过先前已接受的 Event。新的
`PendingSystemInputDelivery` 以单一 durable sequence 恢复、peek、prefix ACK 与 rewrite；adapter 只批量交付
连续 Event，遇到 Gap 则严格在它所在的 journal 位置交付。真实 runtime restart fixture 在 InputEvent
sink 阻塞期间证明后续 Gap 尚未进入 Hub 持久状态，释放 Event 后 Gap 才可见。

### 2026-08-30 — Protocol outbox 交错顺序持久化

第二轮 Spec 复审证明 ingress 的单一 sequence 进入 `CollectorProtocolOutbox` 后又被 Facts/Gaps 两张
list 拆开，flush 固定 Facts 先行；`E1 → Gap → E2` 可变成 `E1 → E2 → Gap`。outbox v1
现在同一 mutation 额外持久 `DeliveryOrder`，ACK、retry rekey、dead-letter 与 capacity eviction 都在原
位更新；client 严格交付 sequence head，不再按类型全量清空。真实 System runtime 测试在 Hub
Event sink 阻塞时确认 outbox 同时持有 2 Facts + 1 Gap，释放后第二个 Event 只能在 Hub Gap
已持久后投影。无 `DeliveryOrder` 的已落盘 v1 不猜原始交错，仅按旧 binary 的
Facts→Gaps 可观察语义恢复；退出盘点已记入 compatibility ledger。验证：Protocol 23/23；
System 交错 runtime 1/1；实际 journal path 阻塞下的 native callback 非阻塞 1/1。

### 2026-08-30 — chunked journal、失败重试与 final closeout

全量 NDJSON rewrite 已由 history-bounded COW chunks 取代：普通 chunk 以 32 KiB 为 target；不可拆的
atomic Segment rotation/Input capacity record 可独占 oversized chunk，下一 mutation 必须轮转，因此不会
重复复制整个历史或裁剪真实 payload。后台 pump 每 100 个 InputEvent 在同一 mutation 中逐项作
Event/Gap capacity 决策；ACK 写 durable tombstone，pending 清空后以保留 active checkpoint 的 reset
令旧 chunks 逻辑不可达并 best-effort 物理删除。Open 只清理本 store 两种 UUID 命名 temp。

复审发现 pump 在 Stage 前 `TryRead` 会在瞬时 I/O 失败时丢掉局部 batch；失败测试在旧逻辑稳定 5 秒
超时。adapter 现在保留 unstaged Segment/Input prefix，只有 durable Stage 成功才清空，原序重试；持续
失败进入 drain 时不得报告 fully drained。相关提交：`24df682`、`1741eda`、`07af2ad`、`eab572b`。
System suite 73/73；store/retry/deadline/cross-process 定向复审 19/19；完整 solution 连续三轮 974/974；
第五轮 Spec 与 Standards 代码轴无 P1/P2。本 issue 状态更新为 `done`。

reset 发布后旧 chunk 删除位于 durable fence 外，但只删除已被 reset 证明逻辑不可达且 index 小于当前
tail 的物理文件；删除失败不影响恢复，deadline 后也不会改变权威 pending/checkpoint 语义。跨平台
directory-entry/power-loss durability 与公共 atomic replacement primitive 仍按上文退出条件保留，不作
超出现有证据的承诺。

### 2026-08-31 — 第二轮独立复审重开

`SystemCollectorIngressStore.Open` 对末条任意 `JsonException` 都会截断，导致完整、带 LF、已 fsync
但含未知字段或错误字段类型的记录被静默删除。只有可证明为 torn/un-terminated 的末条才允许修复；
语义不兼容记录必须保留并 fail 或 quarantine。该边界及 append+restart 回归关闭前，本 issue 恢复为
`needs-triage`。

### 2026-08-31 — ingress 尾部修复收窄到可证明 torn JSON

完整 LF 结尾但带未知字段、错误字段类型的两条测试先稳定 0/2：旧 `Open` 无异常返回并截断已落盘
字节。修复先单独解析 JSON syntax；只有最后一个无 LF 且语法不完整的 record 才按 torn tail 截断，
语法完整后的 schema 反序列化错误不会进入 repair catch。两条记录现在均保留原字节并 fail；既有半行
断尾恢复、合法无 LF 补齐、中间损坏拒绝及修复后 append+restart 一并通过，ingress store 14/14。
本 issue 等待 Feature A 最终完整门禁与双轴复审后再恢复 `done`。

后续 terminal-fence 审计补充确认：即使 repair 判定正确，原地 `SetLength`/追加 LF 仍可被忽略取消的
Initialize 带到 Runtime Dispose 之后。两种 repair 已统一为 COW prepared replacement，并通过 Hub-owned
fence 发布；预先 fence 的两条 red 为 0/2，malformed-tail 另以 temp flush 后 fence→release 的确定性交错
验证原 authoritative bytes 不变。该补充不扩大可修复输入集合，完整语义不兼容记录仍保留并 fail。

### 2026-08-31 — native callback 验证夹具去竞态

最终 solution 第一轮在项目并行负载下暴露测试夹具 TOCTOU：
`NativeInputCallbackDoesNotWaitForIngressJournalPersistence` 在删除 journal 文件后创建同名目录来注入
失败，但 `inputSink.Entered` 只证明 remote projection 已阻塞，并不证明本地 ingress ACK/reset append 已
结束；后台 append 可在 `File.Delete` 与 `Directory.CreateDirectory` 之间重建文件。该轮因此以
`IOException: file already exists` 失败，隔离旧夹具 20/20 全绿，符合负载窗口型失稳。

夹具现改用生产已有的 `beforeIngressCommit` seam：先确定 background commit 已在 temp flush 后阻塞，
再从独立 native callback thread 提交后续 InputEvent，仍要求 2 秒内返回且无异常。它不再修改正在使用的
authoritative path，且直接验证“后台 fsync/commit 阻塞不得反向阻塞平台 callback”。新夹具隔离 20/20；
完整 solution 连续三轮需从零重新计数，完成前 issue 保持 `needs-triage`。
