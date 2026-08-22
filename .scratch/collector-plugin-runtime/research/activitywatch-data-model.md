# ActivityWatch 数据模型与 Collector 协议借鉴

调查日期：2026-08-22  
范围：ActivityWatch 官方文档，以及 `ActivityWatch` GitHub 组织下的 `aw-server-rust`、`aw-client`、`aw-core`、`aw-qt`、`activitywatch` 源码。本文只把 ActivityWatch 当作设计参照，不把它的实现细节自动转化成 Heartbeat 需求。

## 结论先行

ActivityWatch 最值得 Heartbeat 借鉴的不是一套“插件运行时”，而是三件较小且久经使用的设计：

1. Collector（ActivityWatch 称 watcher）是独立生产者，通过很小的 HTTP API 向 server 写入，不必链接 server 内部代码。
2. `timestamp + duration + data` 是一个表达力很强的最小时间事实容器：`duration > 0` 可表达区间，`duration = 0` 可表达时点，任意 JSON `data` 可承载不同领域值。
3. Bucket 把同一来源、同一数据 schema 的事件组织成流；heartbeat 把连续的相同状态压成一个持续增长的区间，显著减少写入与存储。

但它不能直接成为 Heartbeat 的公共 Collector Protocol：

- 常规 `Event.id` 是 server datastore 分配的本地整数，不是 Collector 生成、跨重试/迁移/同步稳定的事实身份；ActivityWatch 自己同步时也明确清空 ID，因为它“不全局唯一”。
- 对没有 ID 的普通插入没有通用去重；相同事件重复提交会得到多行。没有 revision、冲突检测、tombstone 或 exactly-once 契约。
- `data` 的 schema 由 bucket `type` 的约定表达，但没有正式 schema/version 协商。
- Bucket 以 watcher + host 为中心，没有独立的一等 `Subject`；账号、人体、设备等观测主体只能靠 bucket ID 或自由 metadata 约定。
- ActivityWatch 的“扩展”主要是独立可执行文件 + REST API。`aw-qt` 通过 `aw-*` 文件名发现和启停进程，没有 package manifest、签名、能力声明、兼容解析、安装事务或回滚协议。

因此，ActivityWatch 证明了一个小 envelope 可以覆盖 `Segment / Event / Sample` 的**结构形状**，但没有证明这四个字段足以覆盖 Heartbeat 所需的身份、主体、修订、来源、版本和交付语义。

## 1. Event：四个字段很小，但 `id` 的语义尤其重要

ActivityWatch 的持久事件包含：

| 字段 | 语义 |
| --- | --- |
| `id?: integer` | 到达 server datastore 后分配的事件 ID；若写入时主动设置，则替换该 ID 对应的已有记录。 |
| `timestamp` | RFC 3339 UTC 时间，表示事件开始。 |
| `duration` | 秒数；与 timestamp 相加得到结束时间。Rust 实现内部可到纳秒精度。 |
| `data` | 任意 JSON object；同一 bucket 内应遵循该 bucket type 对应的格式。 |

官方数据模型页面给出的 wire shape 是 `{timestamp, duration, data}`，并说明时区 offset 会被丢弃、统一存 UTC；当前 Rust model 则把 server 返回的可选 `id` 也正式列入结构。[官方数据模型](https://docs.activitywatch.net/en/latest/buckets-and-events.html)、[`Event` Rust model](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-models/src/event.rs#L12-L30)

官方 `aw-core` 写入 JSON Schema 甚至只要求 `timestamp`：`duration` 默认 0，`data` 默认空 object，`id` 不在该输入 schema 中。这进一步说明 ID 是存储结果，而非 watcher 必须提供的 occurrence identity。[Event JSON Schema](https://github.com/ActivityWatch/aw-core/blob/11f17e761321b38ae7d49b4728742d6f8182fd4a/aw_core/schemas/event.json#L1-L19)

### `id` 是 server-local row identity，不是 Collector fact identity

正常 watcher 创建 `Event` 时不提供 ID。SQLite 表将 `id` 定义为 `INTEGER PRIMARY KEY AUTOINCREMENT`，插入完成后 server 把 `last_insert_rowid()` 写回返回对象。[events 表](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-datastore/src/datastore.rs#L86-L99)、[插入与回填 ID](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-datastore/src/datastore.rs#L463-L516)

客户端技术上可以提交带 ID 的 Event；server 使用 `INSERT OR REPLACE`，因此相同 ID 会整体替换已有行。官方 model 对此有醒目警告，测试也把“带回 server ID 后重新插入”当作更新方法，而非幂等事实提交。[model 警告](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-models/src/event.rs#L13-L19)、[replace 测试](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-datastore/tests/datastore.rs#L412-L470)

最强的反证来自 `aw-sync`：复制事件到另一 datastore 前，代码明确把 `e.id = None`，注释是“IDs are not globally unique”。目标端会重新分配 ID。[同步时清空 Event ID](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-sync/src/sync.rs#L421-L428)

所以 Heartbeat 不应照搬这个 ID：我们的 `FactId` 若要支撑 retry、离线补传、Hub 重启、跨 Hub 路由和未来修订，就必须由 Collector 侧生成并在同一次 occurrence 的所有重放中保持稳定。存储层 row ID 可以另有其值。

### 高基数 `data`

ActivityWatch 允许 `data` 是任意 JSON object，官方类型示例直接保存 URL、窗口标题、文件完整路径、project 路径等天然高基数字段。[Event types](https://docs.activitywatch.net/en/latest/buckets-and-events.html#event-types)

Rust datastore 把整个 `data` 序列化为 SQLite `TEXT`；当前事件索引只覆盖 `bucketrow/starttime/endtime`，不为每个 JSON key/value 建索引。[序列化写入](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-datastore/src/datastore.rs#L483-L501)、[时间复合索引](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-datastore/src/datastore.rs#L179-L204)

这意味着：高基数值可以作为 payload 保存，不会像 Prometheus label 那样自动制造海量 series；但它仍会增加存储、隐私暴露和查询扫描成本。Heartbeat 应借鉴“原始高基数 payload 与受控索引维度分离”，不能把任意 `attributes` 自动提升为 identity/index dimensions。ActivityWatch 源码中没有通用 payload 大小或基数限制，因此也不能把“可存任意 JSON”误写成容量保证。

还要区分“高基数可保存”和“适合参加 heartbeat equality”：heartbeat 比较整个 `data`。若把随机 ID、逐点 sample value、递增序号等每次变化的字段塞进去，每个 pulse 都会切成新 Event，状态压缩完全失效。[heartbeat 整对象相等](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-transform/src/heartbeat.rs#L12-L17) Heartbeat 若提供 pulse 操作，应由 schema 明确哪些字段构成 state equality，不能默认比较整个任意 payload。

## 2. Bucket：来源流容器，而不是安装、进程或 Subject

官方建议每个 watcher、每台 host 使用一个 bucket，并要求一个 bucket 始终从同一个 source 接收数据。Bucket metadata 主要包含：

- 字符串 `id`；
- `type`：该 bucket 的事件类型/schema 名称；
- `client`：上报客户端软件标识；
- `hostname`：采集发生的设备；
- `created`；
- 自由 JSON `data`；
- server 派生的时间范围 metadata。

来源：[官方 Bucket 文档](https://docs.activitywatch.net/en/latest/buckets-and-events.html#buckets)、[Rust `Bucket` model](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-models/src/bucket.rs#L12-L38)

### Bucket identity 的真实边界

Bucket ID 是客户端选择、单个 datastore 内唯一的字符串；典型命名把 watcher 与 hostname 拼入 ID，例如 `aw-watcher-test_myhostname`。这是一套好用的流命名约定，但不是分离良好的领域身份：source type、producer instance、host、同步 provenance 都可能进入一个字符串。

较新的 Rust server 在 watcher 用特殊 hostname `!local` 创建 bucket 时，会把真实 hostname 写回，并在 bucket `data.device_id` 放 server device ID。这显示项目也在从 hostname 向稳定 device identity 迁移。[`!local` bucket 创建逻辑](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-server/src/endpoints/bucket.rs#L38-L63)

`aw-sync` 拉取远端 bucket 后，会创建 `<original>-synced-from-<origin>` 的本地副本，并在 metadata 放 `$aw.sync.origin`；README 同时明确写着 bucket ID 必须唯一、未来还需要完成 `hostname -> device ID` 迁移。[同步 bucket identity](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-sync/src/sync.rs#L191-L244)、[`aw-sync` 限制](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-sync/README.md#L66-L80)

### 跨设备可以，跨观测主体没有一等模型

ActivityWatch 可以用 `hostname/device_id` 区分设备，并把其他设备的 bucket 同步成本地只读副本；其同步策略通过“每台设备只写自己拥有的文件，其他设备不修改”来规避冲突解决。[`aw-sync` ownership 说明](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-sync/README.md#L60-L80)

但 model 中没有 `SubjectId` 或 `SubjectKind`。若 watcher 观测的是 VRChat Account、Person、wearable，而不是本机，只能把它编码进 bucket ID、type 或自由 data。这对于个人本地 time tracker 足够，却不适合 Heartbeat 的 Headless Hub：一个 Hub 可以运行多个 Collector Instance，并同时观测机器、账号和人；Hub/hostname 不能代替 Subject。

Heartbeat 可把 Bucket 的好部分重述为 **Fact Stream**：一个稳定的 Collector Instance 在一个明确 Subject 下产生某个 schema 的事实流；Package、Activation 和 Hub 则是另外的身份轴。

## 3. Heartbeat：连续状态压缩，不是通用事实 upsert

### 合并规则

server 只尝试把新 heartbeat 与该 bucket 的“最后一个事件”合并。当前 Rust 算法要求：

1. 两者 `data` 做深度相等比较；
2. 旧事件 timestamp 不晚于新 heartbeat timestamp；
3. 新 heartbeat timestamp 不超过 `old.end + pulsetime`；
4. 合并后 start 取较早 start，end 取两者较晚 end，duration 为 `end - start`。

来源：[官方简化说明](https://docs.activitywatch.net/en/latest/buckets-and-events.html#heartbeats)、[实际 merge 函数](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-transform/src/heartbeat.rs#L3-L59)

典型 watcher 每次发送 `duration = 0` 的 pulse。假设已有事件是 `10:00:00 + 0s`，10:00:05 收到相同 data 且 pulsetime 至少 5 秒，合并结果会保留 10:00:00，并把 duration 扩为 5 秒。若 incoming heartbeat 自身带 duration，则新 end 是 `timestamp + incoming.duration`，所以也可以扩到 pulse 的结束时间，而不只是 pulse timestamp。

### server 如何原地延长 duration

合并成功时，transform 产生一个暂时无 ID 的 merged Event；datastore 取出旧事件的 server ID，用 `UPDATE events SET starttime/endtime/data WHERE bucketrow=? AND id=?` 更新同一行，再把原 ID 放回缓存。合并失败则普通插入新行。[heartbeat datastore 流程](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-datastore/src/datastore.rs#L588-L695)

这适合表达“当前窗口仍是 X”“仍处于非 AFK 状态”之类的连续状态观测。它的边界也很清楚：

- equality 是整个 `data` 的字面相等，不是 schema 定义的业务等价；任何字段变化都会切段。
- 只与最后一条比较，不会在任意历史位置做 interval reconciliation。
- 它没有 Collector revision 或 monotonic sequence；“更新成功”依赖 server 当前最后行与缓存状态。
- pulse 是写入命令，不是持久事实种类；持久结果仍是普通 Event。

对 Heartbeat 更稳妥的借鉴是：若某种 Collector 输出的是“持续观察到相同状态”，可以在**该 schema 明确授权**时提供 `ObserveState`/`ExtendOpenSegment` 一类操作；不能把“任意相同 payload 自动合并”作为所有 Fact 的默认协议。

## 4. 插入、重复、更新和删除

### 普通插入与去重

events 表除 server integer ID 外没有 timestamp、duration 或 data 的唯一约束。没有 ID 的 `POST events` 每次都会插入新行，因此完全相同的 Event 重放也会重复。`Event.PartialEq` 虽然比较 timestamp/duration/data 而忽略 ID，但 datastore 普通插入并没有用它做去重。[表结构](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-datastore/src/datastore.rs#L86-L99)、[`Event` equality](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-models/src/event.rs#L50-L55)

Heartbeat 的同请求重试在一个非常窄的条件下可能自然收敛：若它仍与正确的最后一行相同且时间相容，`max(end)` 再算一次不会继续增长。但这不是带稳定 request/fact ID 的幂等保证；遇到 data 变化、乱序、并发 writer 或最后行已变化，就可能新增行。

### 更新与删除

- 没有独立的通用 `PUT/PATCH Event` endpoint。知道 server-local ID 的客户端可重新 POST 带 ID 的 Event，触发 `INSERT OR REPLACE`。
- 有 `DELETE /buckets/<bucket>/events/<event_id>`，server 按 bucket + ID 物理删除。
- 没有 revision、expected-version/CAS、冲突记录、tombstone/retraction 事实或删除传播契约。

来源：[REST routes](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-server/src/endpoints/bucket.rs#L115-L185)、[delete SQL](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-datastore/src/datastore.rs#L518-L540)

`aw-sync` 当前明确不支持修改或删除事件的同步；这与它清空 ID、只推进追加 cursor 的设计一致。[`aw-sync` limitations](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-sync/README.md#L72-L80)

因此 Heartbeat 不能用 ActivityWatch 的 server ID/replace/delete 语义满足稳定采集协议。至少需要裁决：同 `FactId` 同内容是否幂等、不同内容是 conflict 还是更高 revision、撤销如何传播，以及旧 revision 如何防止覆盖新 revision。

## 5. Watcher、server 与进程管理器的职责

ActivityWatch 的职责分离相当清楚：

- watcher 观察外部世界并发出 Events/heartbeats；server 自身不采集；
- `aw-server` 保存和查询 bucket/events，并托管 Web UI；
- `aw-client` 封装 REST；不同语言可自行实现 client；
- `aw-qt` 是本地 service manager/tray，负责发现、启动和终止 server/watchers；
- browser extension 由 Browser 管理执行，再向 server 写 heartbeat。

来源：[官方 Architecture](https://docs.activitywatch.net/en/latest/architecture.html)、[主仓库架构图](https://github.com/ActivityWatch/activitywatch/blob/a414628c245e5ceec5898857816c7fa18c9fb9f9/README.md#L182-L241)

这和 Heartbeat 正在形成的两轴模型很相似：artifact delivery 与 execution driver 不必一致。特别是 browser watcher，Browser 拥有执行生命周期，而 server 只拥有摄入边界。

不过 `aw-qt` 的 module 管理很轻：它递归查找 `aw-*` 可执行文件，区分 bundled/system，启动时直接执行路径（测试模式只额外传 `--testing`），停止时 terminate + wait。没有读取 manifest 或进行协议握手。[module discovery](https://github.com/ActivityWatch/aw-qt/blob/45f4c2f66e9a76a0e98077e32f254bde74352a03/aw_qt/manager.py#L29-L147)、[start/stop](https://github.com/ActivityWatch/aw-qt/blob/45f4c2f66e9a76a0e98077e32f254bde74352a03/aw_qt/manager.py#L213-L293)

这可作为“进程外 Collector 很简单”的正面证据，不可作为 Heartbeat Package Runtime 已解决的证据。它没有制品哈希、签名、兼容约束、候选激活、健康检查提交或失败回滚。

## 6. 版本与“插件协议”

ActivityWatch 客户端统一使用 `/api/0/...` REST endpoint。官方文档截至当前仍警告 API 在开发中、可能变化、尚未冻结；server `/info` 会返回产品 version，但 Python client 固定拼 `/api/0/`，没有看到基于 server version 的 capability negotiation。[REST API 警告](https://docs.activitywatch.net/en/latest/api/rest.html)、[Python client 固定 API path](https://github.com/ActivityWatch/aw-client/blob/2c88b87202f787c6150b2f520f50c92c84f18988/aw_client/client.py#L107-L153)、[server info](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-server/src/endpoints/mod.rs#L109-L121)

Bucket `type` 被描述为其中事件的 schema，官方鼓励为 watcher 建立标准，例如 `web.tab.current`、`currentwindow`、`afkstatus`。但它只是一个字符串约定：Event envelope 没有独立 `schemaVersion`，Bucket 也没有兼容范围或迁移声明。[Event types](https://docs.activitywatch.net/en/latest/buckets-and-events.html#event-types)

官方整套发行版通过主仓库的 git submodules 固定各组件源码版本并一起发布；第三方 watcher 则可独立安装并使用 REST。[主仓库 bundle 说明](https://github.com/ActivityWatch/activitywatch/blob/a414628c245e5ceec5898857816c7fa18c9fb9f9/README.md#L221-L264)、[`.gitmodules`](https://github.com/ActivityWatch/activitywatch/blob/a414628c245e5ceec5898857816c7fa18c9fb9f9/.gitmodules)

所以 ActivityWatch 提供的是**数据写入 API + watcher conventions**，不是 Heartbeat 此处所说的完整插件协议/运行时。Heartbeat 仍需自己的：

- Package manifest 与 immutable artifact identity；
- host/protocol compatibility ranges；
- outputs/capabilities 声明；
-配置 schema/version；
- handshake 与实际 capability/version 回报；
- lifecycle/health/failure phases；
- 签名、安装、切换和回滚语义。

## 7. 离线、重试与乱序

### watcher 到 server 的离线队列

Python `aw-client` 的 `queued=True` heartbeat 有两层优化：

1. 客户端先在内存中按相同 heartbeat 规则预合并；达到 `commit_interval` 或 data 改变后，才把请求放入队列。
2. 队列使用持久 SQLite FIFO 保存待发 HTTP heartbeat。连接失败/timeout 保留当前请求并重试；HTTP 500 重试；400 与其他被判断为不可重试的错误会丢弃该请求。

来源：[queued heartbeat/pre-merge](https://github.com/ActivityWatch/aw-client/blob/2c88b87202f787c6150b2f520f50c92c84f18988/aw_client/client.py#L230-L281)、[持久 request queue 与 retry 分类](https://github.com/ActivityWatch/aw-client/blob/2c88b87202f787c6150b2f520f50c92c84f18988/aw_client/client.py#L425-L580)

这提供了实用的 at-least-attempted delivery，但不是端到端 exactly-once：请求没有稳定 occurrence ID，连接超时不能证明 server 是否已提交；普通 Event API 也不使用该队列。另一个实现细节是尚未达到 commit 条件的最后 heartbeat 保存在 `last_heartbeat` 内存 dict 中，而 `disconnect()` 只停止 request queue，没有显式把这些内存 tail 全部刷入磁盘；不能把它描述为任意退出情况下零丢失。[connect/disconnect](https://github.com/ActivityWatch/aw-client/blob/2c88b87202f787c6150b2f520f50c92c84f18988/aw_client/client.py#L374-L396)

### server 的乱序行为

普通 Event 插入没有 source timestamp 单调性检查，查询按 `starttime DESC` 排序，所以 datastore 结构上可接受历史/乱序记录。[event query](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-datastore/src/datastore.rs#L754-L853)

Heartbeat 则是最后行优化：如果当前最后事件的 timestamp 晚于迟到 heartbeat，merge 函数拒绝合并，server 随后把迟到 heartbeat 插成独立 Event。它不会回头寻找正确相邻历史段，也不会修复历史重叠。[乱序拒绝条件](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-transform/src/heartbeat.rs#L19-L32)、[merge 失败后插入](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-datastore/src/datastore.rs#L673-L695)

Event 只有 source `timestamp`，没有 `observedAt/receivedAt`，因此无法从事实本身区分“当时发生、现在补传”与“现在发生”。

### 设备间同步的乱序限制

`aw-sync` 是 beta：每台设备先把自己拥有的 buckets 写到 device-specific staging DB，再由 Syncthing/rsync/Dropbox 等同步目录。目标端以现有最新事件的 end 作为 resume cursor，只拉取其后的 source events；事件复制时重新分配 ID。[官方 Syncing 文档](https://docs.activitywatch.net/en/latest/syncing.html)、[sync resume 与 copy](https://github.com/ActivityWatch/aw-server-rust/blob/b2e2dee7eead1152df5489e60ccdf78328127352/aw-sync/src/sync.rs#L358-L428)

这是一种 append-in-event-time 的假设：若源端后来新增一条早于目标 cursor 的历史事件，它不属于该增量算法能可靠发现的范围。再结合“不支持修改/删除同步”，ActivityWatch 的 sync 不应被当作 Heartbeat 的迟到修订模型。

Heartbeat 应保留 ActivityWatch 的“本地 durable queue + 批量/预合并”思路，但必须把顺序与正确性分开：

- 每个 Fact 有跨重试稳定 ID；
- source occurrence time 与 Hub observed/received time 分离；
- queue retry 可以重复，server ingest 必须幂等；
- 同 ID 不同 revision/conflict 有明确规则；
- 迟到是否接收、是否重算派生视图是产品策略；
- open Segment 的增长必须有 monotonic revision/sequence，不能让迟到旧 snapshot 回退新状态。

## 8. 能否表达 Segment / Event / Sample

| Heartbeat 事实家族 | ActivityWatch 表达方式 | 能力判断 | 缺失的领域语义 |
| --- | --- | --- | --- |
| `Segment` | `timestamp + duration>0 + data`；连续状态可由 heartbeat 延长。 | 原生且最匹配。 | 稳定 Collector FactId、revision、close/correct/retract、Subject、observed time。 |
| `Event` | `duration=0 + data`。 | 结构上完整，可表达离散发生。 | occurrence ID、event name/schema version、幂等与修订。 |
| `Sample` | `duration=0`，把 metric name/value/unit 等放入 `data`；区间聚合也可借 duration 表达。 | 只能“装下”，没有 metric semantics。 | stream identity、Gauge/Sum/Histogram、delta/cumulative、monotonic、start time/reset、unit、missing/stale、维度基数规则。 |

因此，`timestamp + duration + data` 可以表述几乎所有行为/事件的**序列化外形**，但 `data` 中“什么都能放”不等于协议理解“什么都能正确合并、去重、修订和查询”。

这对 Heartbeat 当前“用最小字段保留最大内容”的启示是：字段少不应等于语义隐式。可以保持一个小的公共 envelope，但应让少数正确性字段一等化，而不是塞进任意 payload：

- `FactId`（Collector 生成，稳定）；
- `SubjectId`；
- `CollectorInstanceId`；
- `Kind + SchemaId/SchemaVersion`；
- source time（instant 或 interval）与 observed/received time；
- revision/conflict/retraction 所需字段；
- typed payload，以及与 payload 分离的受控 index dimensions。

`Segment / Event / Sample` 仍适合做 Heartbeat 的公共产品语言，但 Sample 更准确的协议名需要再看：如果首版就希望覆盖心率、步数累计、直方图等，`Measurement` 或 `MetricPoint` 比“Sample = timestamp + number”更不容易误导。

## 9. 对 Heartbeat 的具体取舍

### 建议吸收

1. **独立 Collector + 小 HTTP 协议**：进程外隔离天然支持不同语言，也让 Collector 崩溃不直接拖垮 Hub。
2. **流级 metadata**：把不会随每条 Fact 变化的 schema/source 信息放在 stream/instance 层，避免重复。
3. **小公共时间 envelope + typed payload**：保留 ActivityWatch 的表达力，但补齐稳定身份、Subject、schema version 和 provenance。
4. **显式的 state observation 压缩操作**：对于连续状态 schema，允许 pulse/extend；将其限定为一种写入语义，而不是所有事实的自动猜测。
5. **本地 durable queue 与 client-side compaction**：适合 Desktop Agent 和暂时离线的 Collector，但 server 必须以稳定 FactId 提供幂等。
6. **高基数 payload 不自动索引**：URL、world name、窗口标题等保留原值；索引维度使用单独、受控 vocabulary。
7. **执行所有权诚实分离**：Browser 管 browser extension，Hub 可以管理摄入与部分 artifact，不宣称自己已经停止外部代码。

### 不建议照搬

1. server-local integer Event ID 充当协议身份；
2. `watcher_hostname` 或一个 Bucket ID 同时承担 source、instance、device、subject 和 sync provenance；
3. 无 version 的自由 JSON `data` 作为唯一扩展机制；
4. “与最后事件 data 相同就 merge”作为通用 upsert；
5. 没有 revision/tombstone/conflict 的 overwrite/delete；
6. 只靠文件名发现可执行文件并称之为插件运行时；
7. 把本地 loopback-only REST 安全假设带到远程 Headless Hub/Heartbeat Server；ActivityWatch 官方 REST 文档也明确说当前安全主要依赖不允许非 localhost 连接。[REST Security](https://docs.activitywatch.net/en/latest/api/rest.html#rest-security)

## 最终判断

ActivityWatch 是 Heartbeat Collector 数据面的重要先例，但不是包管理与运行时地基的先例。

它验证了：

```text
Collector/Watcher -> 小协议 -> Hub/Server -> 时间流存储
```

以及：

```text
timestamp + duration + typed payload
```

足以作为三类时间事实的共同几何基础。

它也清楚暴露了如果过度追求“最少字段”会缺什么：全球稳定事实身份、Subject、source/observed 双时间、schema/version、幂等、修订/撤销、迟到协调，以及 Package/Activation 的独立身份。

所以对 Heartbeat 的最佳结论不是把三种事实压成 ActivityWatch `Event` 并结束设计，而是：**共享最小、明确的 Fact Envelope；Segment/Event/Measurement 保持类型化语义；stream、subject、collector 和 package/runtime 身份分层；pulse 作为某些 schema 的显式摄入操作。**
