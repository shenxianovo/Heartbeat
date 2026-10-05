# ActivityWatch 的记录模型与 Heartbeat 对照

调研日期：2026-09-28。本文是源码调研，不是 Heartbeat 的架构决策，不改变现有契约或 ADR。

## 范围与结论

ActivityWatch 把核心收敛为 **Bucket → Event**：Watcher 定期报告状态，客户端和服务端用 heartbeat 合并相同读数，查询层再组合窗口、AFK、浏览器等数据，生成用于统计的解释。它解决了大量相同采样的压缩、短暂离线后的补传和多来源分析，但没有用同一套底层语义解决 Heartbeat 所关心的全部问题。

最重要的差别是：**ActivityWatch 的连续性主要由“值相同、时间足够近、当前最后一条”判断；Heartbeat 的已接受规则由 Collector 判定是否属于同一段连续观测，再以稳定 Record 身份续期。** 两者都可能使用 `max(end)`，但合并对象的身份来源不同，不能据此认为具有相同的乱序、重启或断采语义。[AW 心跳函数][aw-heartbeat]、[AW Rust 写入流程][rust-heartbeat]、[Heartbeat ADR-0002](../adr/ADR-0002-monotonic-record-extension.md)

本地源码位于 `/Users/bytedance/Code/Research/activitywatch`。分析以主仓固定 gitlink 为准，而非各子仓最新分支：

| 仓库 | 固定 commit |
| --- | --- |
| activitywatch | `f857d7119a315ab41bd2e678a389f24be42e883e` |
| aw-core | `d6ec34c6a2c7d085cc22deaec0071559cdce7661` |
| aw-server（Python） | `b19e5b01a1d284b6399e17ac013d023251222139` |
| aw-server-rust | `e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18` |
| aw-client | `f80900ea38ef9411e2600b633877c7f9922f1133` |
| aw-watcher-window | `a82b6d13c9fc7c87fb3020184e85676d5ccf70f0` |
| aw-watcher-afk | `c1a020927028016a7af4041d795fe30d136fe864` |
| aw-watcher-input | `9bb5045456524b215ae11f422b80ec728c93bac7` |

本文将“源码事实”“由源码得出的推论”“实际运行验证”分开描述。官方文档用于理解概念；涉及实现细节时，固定 commit 的代码优先。

## 1. 用更少的层表达来源与时间

```text
ActivityWatch server / datastore
└── Bucket：id、type、client、hostname、data 等元数据
    └── Event：id、timestamp、duration、data
```

**源码事实：** Bucket 同时容纳来源元数据和事件类型；Event 是带 JSON 内容的时间片段。Rust 的 `id` 是服务端存储分配的整数；`timestamp + duration` 给出结束时刻，`duration` 缺省为 0。Event 没有独立的 `observed_at`、`received_at`、`time_mode`、`end_mode` 字段；Bucket 没有独立的协议版本字段。[Bucket][rust-bucket]、[Event][rust-event]

**对照推论：** Bucket 近似承载 Heartbeat 的一部分 Collector 身份和 Track 契约，但不存在一一映射。Heartbeat 将“实现绑定哪个 Target”和“使用哪个协议、具有哪种时间行为”拆为两层；ActivityWatch 让 Watcher 选择 Bucket ID，并通过 `type/client/hostname/data` 约定用途。上述核心模型也没有显式的 Owner → Timeline 层；不应把一个 Bucket 解释成一个人的完整 Timeline。[Heartbeat 领域语言](../../CONTEXT.md)、[AW Bucket][rust-bucket]

**时间语义：** `duration = 0` 既能表示瞬时记录，也能是尚未被后续 heartbeat 延长的状态；模型不强制区分这两种含义。结束时间不靠下一条不同值事件自动推导，心跳协议只在相同值可合并时延长已有事件。Python 序列化规范化到 UTC，并把 timestamp 精度截到毫秒；Rust Event 的 duration 表达可到纳秒。[Python Event][py-event]、[Rust Event][rust-event]、[AW 心跳函数][aw-heartbeat]

## 2. heartbeat：状态采样合并为区间

**源码事实：** 对已有事件 `L` 和新心跳 `H`，Python 与 Rust 的基本条件一致：

```text
L.data == H.data
L.start <= H.start <= L.end + pulsetime

符合条件：start = L.start，end = max(L.end, H.end)
不符合条件：插入新 Event
```

`pulsetime` 是“允许相邻同值采样被合并的最大间隔”，不是给最后一次采样自动增加的持续时间。新 heartbeat 可以自带非零 duration；客户端预合并产生的区间也能继续参与服务端合并。[Python 合并][aw-heartbeat]、[Rust 合并][rust-transform]

例如 `pulsetime = 5 秒`，零时长的 Safari 读数依次出现在 `0s、1s、2s`，得到 `[0s,2s]`。直到 `20s` 才收到下一个同值采样时，超出窗口，另建 Event。若 `4s` 就恢复采样，仍可能连接为 `[0s,4s]`。

**对照推论：** 这个机制把短暂空白当作采样不确定性，并用“同值且够近”估计连续性。Heartbeat 的 ADR-0002 则要求实际断采后创建新 Record，即使值相同也不能补成一段。AW 的通用 heartbeat 不认识 Collector 的采集会话、断采边界或 Observation Status，所以无法仅凭该算法区分“正常轮询间隔”和“短暂中断后恢复”。[AW 心跳函数][aw-heartbeat]、[Heartbeat ADR-0002](../adr/ADR-0002-monotonic-record-extension.md)

## 3. 乱序、重试、重启：`max(end)` 的保证有边界

**源码事实：** 两套服务端均优先取每个 Bucket 缓存的最后一条心跳；没有缓存时从数据库取最新一条。它们不会为了迟到心跳搜索完整历史、找到同一份逻辑结果。[Python 流程][py-heartbeat]、[Rust 流程][rust-heartbeat]

因此需要区分：

- **当前合并区间内部的迟到确认：** 起点仍不早于当前事件起点、值相同且在窗口内，`max(end)` 避免缩短。
- **跨过状态切换后重放旧确认：** 可能与当前最后事件不匹配，从而插入新 Event；这不是对任意顺序都成立的幂等协议。
- **服务端重启：** 通过数据库最新事件恢复合并候选。它能继续旧区间，但不是一次新的采集会话边界。
- **Watcher 重启：** 若仍写同一 Bucket、读数相同且处于 pulsetime 内，通用服务端仍可能合并；重启本身没有进入 Event 模型。

以上为由代码得出的行为判断。[Python 合并][aw-heartbeat]、[Python 流程][py-heartbeat]、[Rust 流程][rust-heartbeat]

**Python 与 Rust 的差异：** 固定版本 Rust 在合并后按候选事件的 ID 更新对应数据库行。Python 的 Peewee `replace_last` 则重新选择数据库中 timestamp 最大的一行，而不是按缓存事件 ID 替换；当迟到事件成为缓存候选而数据库仍有更晚事件时，存在更新错行的风险。不能将两套服务端视为完全一致，也不能把这个实现风险泛化成 AW 概念模型的必然要求。[Rust replace_last_event][rust-replace]、[Python replace_last][py-replace]

**实际运行验证：** 在临时 SQLite 上调用真实 Python `ServerAPI.heartbeat`，依次输入 `A@0s、A@10s、B@20s、A@5s、A@6s`，`pulsetime=15`，最后 `B@20s` 确实被覆盖成 `A@[5s,6s]`。这是该固定 Python/Peewee 实现的复现结果；Rust 没有运行同一实验，不能声称 Rust 也出现该结果。脚本与输出见下方验证记录。

## 4. 历史更正：有物理覆盖能力，仍要区分业务身份

**源码事实：** Rust 普通 `POST /buckets/<id>/events` 进入 `insert_events`，使用 `INSERT OR REPLACE`。无 ID 时分配新 ID，带既有 ID 时可覆盖 timestamp、duration 和 data。因此它能表达“步数从 6000 改为 5500”或“区间移动、缩短”的存储结果；并不限于心跳续期。[REST 写入和删除][rust-rest]、[Rust insert_events][rust-insert]

Python 的 Peewee 路径同样区分有 ID 的更新和无 ID 的新增，通过已有 ID 保存对象；不能只因 REST 路由叫 create 就断言不支持修改。[Python insert][py-insert]、[Python EventModel][py-eventmodel]

**边界：** Event 的存储 ID 不是 Collector 在首次交付前生成的稳定逻辑结果 ID。Rust 数据表 ID 是数据库范围的自增主键，不能把其他数据库的 Event ID 当作天然可移植的业务身份。普通无 ID 插入也不因 timestamp/data 相同而自动去重。[Rust 表结构][rust-schema]、[Rust insert_events][rust-insert]

**对照推论：** “可以覆盖某行”只回答如何改变当前值，不等于已经回答哪次更正更新、旧重传能否覆盖更正、删除如何防止恢复等问题。Heartbeat ADR-0006 已明确要求结果更正语义，而其新旧判定和交付设计仍待确定；可以参考 AW 的分离接口，但不能只复制一条 upsert。[Heartbeat ADR-0006](../adr/ADR-0006-result-correction-semantics.md)

## 5. 查询层承担“活动”的解释

**源码事实：** 查询语言能读取多个 Bucket，再做时间相交、分类、按字段合并及累计时长。官方示例将窗口事件与 `status = not-afk` 的区间相交，再按 app 汇总；“某应用用了多久”是经过这些规则计算出来的结果。[官方查询示例](https://docs.activitywatch.net/en/latest/examples/working-with-data.html)、[查询函数][py-query]、[按字段累计][py-aggregate]

```text
窗口读数 + AFK 读数
    ↓ 时间相交、分类、可选 gap 处理
用于展示的活动区间 / 按应用累计时长
```

值得注意的是 `flood`：固定版本默认处理不超过 5 秒的短空白，同值邻居可合并，异值邻居把空白分到中点；重叠且异值时，后来的事件优先。Python 在事件副本上处理，Rust 查询返回变换后的值，不在这些函数中写回原始事件。[Python flood][py-flood]、[Rust 查询 flood][rust-query-flood]

**对照推论：** 这是一个对 Heartbeat 有用的责任划分参考：让统计或回放解释显式消费观测，不需要把“工作”“专注”写进底层事实。同时，`flood` 的填空规则属于估计，应与保存的观测分开；Heartbeat 已定义 Observation Gap 为未知，不能直接把这类填空结果升级为已确认的 Record。[Heartbeat 领域语言](../../CONTEXT.md)

另一个读取细节：AW 按时间窗口读取时，会把跨边界 Event 的 timestamp/duration 裁到请求窗口。Rust 和 Python Peewee 均有这一行为。因此“读取某天的数据再带原 ID 写回”可能意外把完整事件缩短。涉及历史编辑时，应先按 ID 读取完整对象，不能假定列表返回的时间就是原始存储时间。[Rust 裁剪][rust-clip]、[Python 裁剪][py-clip]

## 6. 离线交付：客户端队列与服务端提交是不同边界

**源码事实：** Python `aw-client` 的 `queued=True` 先在内存 `last_heartbeat` 中预合并；达到 commit_interval 或无法继续合并时，才把区间放入 `FIFOSQLiteQueue`。默认 commit_interval 为 10 秒。队列成功交付后确认完成，部分错误保留请求重试，另一些错误结束该请求；不能描述为“一切失败都会无限重试”。`disconnect` 停止队列线程，没有在该路径 flush 尚在 `last_heartbeat` 的尾部。[预合并][client-premerge]、[默认配置][client-config]、[持久队列与错误处理][client-queue]、[disconnect][client-disconnect]

**实际运行验证：** 调用真实 client `heartbeat` 方法、用队列替身记录入队，连续 `0s…9s` 的同值采样尚未入队，`10s` 的采样才使 duration=10 的区间入队。这验证了预合并边界，没有验证 SQLite 队列断电恢复。

**Rust 源码事实：** 普通 heartbeat 在工作线程执行后即可 ACK，数据库随后批量 commit；`ForceCommit/Close` 才明确等待 commit 后 ACK。源码说明 commit 失败时，已返回成功的数据可能回滚而客户端不再重试。因此成功 heartbeat 响应不能直接当作持久提交回执。[Rust 批量提交][rust-commit]

**对照推论：** AW 的选择减少了高频写盘与等待；Heartbeat Hub 的“接管成功即 SQLite 持久接收”契约在另一个位置划定交付责任。二者都有队列，不代表具有同样的接管承诺。此处未做 Rust 进程崩溃或断电实验。[Heartbeat ADR-0009](../adr/ADR-0009-delivery-belongs-to-hub.md)

## 7. Watcher 的数据含义没有完全留给通用层

- **窗口：** Python watcher 的 Bucket 使用 client 名与 hostname，`currentwindow` 同时保存 app 和 title；任一字段变化都可能阻止同值心跳合并。采集失败时该路径不发读数；macOS 另有 Swift 分支，不能用 Python 轮询细节概括所有平台。[窗口身份与平台分支][window-main]、[窗口采样][window-poll]
- **AFK：** watcher 根据输入空闲阈值报告 `afk/not-afk`，会把进入 AFK 的时间回溯到最后输入时刻；默认 timeout 180 秒、poll 5 秒。这是一种采集端推定，不等同于 Heartbeat 的明确系统 Away Signal。[AFK 状态机][afk-main]、[AFK 默认配置][afk-config]
- **输入：** 该版本 `aw-watcher-input` 每 5 秒汇总键鼠计数；它不是 Heartbeat 当前的逐次非文本物理 Input Event。[输入采集器][input-main]

**设备身份：** AW Rust 服务端另有数据目录中的 `device_id` 文件，首次生成 UUID，后续读取同一文件。保留文件可延续身份，但不能由此推断它提供“系统重装后仍识别同一物理设备”的契约，也不能把 hostname、Bucket ID 与 device_id 混为一谈。[device_id][rust-device]

## 8. 跨设备同步：用单写者降低冲突复杂度

**源码事实：** `aw-sync` 围绕同步目录工作，可配合外部文件同步工具；每台设备只写自己拥有的文件，其他设备读取镜像。其 README 明确以这种单写者约束减少冲突处理。[同步设计][sync-readme]

该固定版本已经包含有限的历史编辑同步，不能笼统说“完全不支持历史修改”：它从目标续传游标向前回看 7 天，按 `(timestamp, duration)` 匹配，来源对自身 Bucket 有权威性，内容发生改变时以新插入、再删旧数据的方式协调。这个身份规则不等价于稳定逻辑 Event ID，任意移动时间、缩短历史区间不在该相同身份匹配的保证内；持续末条事件的 duration 增长另走增量 heartbeat 路径。[回看窗口][sync-lookback]、[同步协调代码][sync-edits]

README 仍明确声明独立事件删除不传播；更早的历史修改不在上述回看窗口内。仓库中的 `sync-v2` 是尚未接到命令的 writer 功能，不能仅因目录存在就算作当前同步能力。[同步限制][sync-readme]、[v2 状态][sync-v2]

**对照推论：** AW 通过限制写入权威与编辑窗口解决常见跨设备镜像需求，没有把通用历史结果更正和删除防重传化解为一个无条件成立的分布式协议。

## 验证记录与未验证范围

实验日期：2026-09-28。独立环境位于 `/Users/bytedance/Code/Research/activitywatch-analysis`；没有改动 Heartbeat 实现。

| 验证 | 结果与边界 | 可复查证据 |
| --- | --- | --- |
| 心跳纯函数、客户端入队阈值 | 边界内合并、超过窗口不合并、title 改变不合并、已覆盖迟到确认不缩短、更早起点不合并；客户端预合并采用队列替身 | `probe_core_client.py`、`probe_core_client.json` |
| Python ServerAPI + Peewee | 临时真实 SQLite 复现乱序覆盖；同 ID 修改内容、移动及缩短区间；范围 GET 裁剪；删除 | `probe_server.py`、`probe_server.json` |
| 上游核心测试 | `tests/test_heartbeat.py` 与 `tests/test_flood.py` 通过；命令见下方，输出见日志 | `upstream-core-tests.txt` |
| 版本与依赖 | 保存主仓、子仓 revision 及隔离环境依赖 | `source-revisions.txt`、`python-dependencies.txt` |

复跑自写实验：

```sh
/Users/bytedance/Code/Research/activitywatch-analysis/.venv/bin/python /Users/bytedance/Code/Research/activitywatch-analysis/probe_core_client.py
/Users/bytedance/Code/Research/activitywatch-analysis/.venv/bin/python /Users/bytedance/Code/Research/activitywatch-analysis/probe_server.py
```

复跑上游测试：

```sh
cd /Users/bytedance/Code/Research/activitywatch/aw-core
/Users/bytedance/Code/Research/activitywatch-analysis/.venv/bin/python -m pytest -q tests/test_heartbeat.py tests/test_flood.py
```

ServerAPI 实验直接调用真实方法，并绕过构造器以避免写应用设置；没有启动 HTTP 服务。Rust、跨设备同步、系统 Watcher、客户端完整故障恢复未进行端到端运行；相应结论是固定源码和项目自身文档的分析。本文没有声称对 ActivityWatch 做了完整可靠性或安全审计。

## 对 Heartbeat 的启示（研究判断，未作实施决定）

1. **可以学习的简洁性：** Bucket/Event、同值采样压缩以及独立查询变换，使 Watcher 接入成本较低；可以用它反问 Heartbeat 每个抽象是否承担了明确且当前需要的语义。
2. **值得保留的差别：** 稳定 Record 身份与断采边界，让 Heartbeat 不依赖“最近一条同值记录”猜测续期对象；这对离线补传、多个观测对象和显式 Observation Gap 有实际价值。
3. **更正能力需要完整定义：** AW 证明可变 Event 并不妨碍一个简单的事件存储模型，但普通 upsert 与具备新旧判定、删除传播的更正协议是两件事。
4. **查询规则应可解释：** AFK 过滤、分类、短空白填补会改变统计含义；若 Heartbeat 增加类似能力，应在读取方或派生结果中明确表达规则。

上述判断没有要求替换 Heartbeat 的四层模型，也未修改任何架构契约。

## 固定源码来源

[aw-heartbeat]: https://github.com/ActivityWatch/aw-core/blob/d6ec34c6a2c7d085cc22deaec0071559cdce7661/aw_transform/heartbeats.py#L26-L56
[py-event]: https://github.com/ActivityWatch/aw-core/blob/d6ec34c6a2c7d085cc22deaec0071559cdce7661/aw_core/models.py#L25-L96
[rust-bucket]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-models/src/bucket.rs#L12-L38
[rust-event]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-models/src/event.rs#L12-L46
[rust-transform]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-transform/src/heartbeat.rs#L12-L58
[rust-heartbeat]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-datastore/src/datastore.rs#L942-L992
[py-heartbeat]: https://github.com/ActivityWatch/aw-server/blob/b19e5b01a1d284b6399e17ac013d023251222139/aw_server/api.py#L308-L350
[rust-replace]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-datastore/src/datastore.rs#L885-L939
[py-replace]: https://github.com/ActivityWatch/aw-core/blob/d6ec34c6a2c7d085cc22deaec0071559cdce7661/aw_datastore/storages/peewee.py#L382-L397
[rust-rest]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-server/src/endpoints/bucket.rs#L159-L215
[rust-insert]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-datastore/src/datastore.rs#L720-L810
[rust-schema]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-datastore/src/datastore.rs#L94-L110
[py-insert]: https://github.com/ActivityWatch/aw-core/blob/d6ec34c6a2c7d085cc22deaec0071559cdce7661/aw_datastore/storages/peewee.py#L338-L369
[py-eventmodel]: https://github.com/ActivityWatch/aw-core/blob/d6ec34c6a2c7d085cc22deaec0071559cdce7661/aw_datastore/storages/peewee.py#L138-L156
[py-query]: https://github.com/ActivityWatch/aw-core/blob/d6ec34c6a2c7d085cc22deaec0071559cdce7661/aw_query/functions.py#L152-L205
[py-aggregate]: https://github.com/ActivityWatch/aw-core/blob/d6ec34c6a2c7d085cc22deaec0071559cdce7661/aw_transform/merge_events_by_keys.py#L9-L40
[py-flood]: https://github.com/ActivityWatch/aw-core/blob/d6ec34c6a2c7d085cc22deaec0071559cdce7661/aw_transform/flood.py#L11-L127
[rust-query-flood]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-query/src/functions.rs#L269-L297
[rust-clip]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-datastore/src/datastore.rs#L277-L310
[py-clip]: https://github.com/ActivityWatch/aw-core/blob/d6ec34c6a2c7d085cc22deaec0071559cdce7661/aw_datastore/storages/peewee.py#L478-L490
[client-premerge]: https://github.com/ActivityWatch/aw-client/blob/f80900ea38ef9411e2600b633877c7f9922f1133/aw_client/client.py#L287-L316
[client-config]: https://github.com/ActivityWatch/aw-client/blob/f80900ea38ef9411e2600b633877c7f9922f1133/aw_client/config.py#L31-L43
[client-queue]: https://github.com/ActivityWatch/aw-client/blob/f80900ea38ef9411e2600b633877c7f9922f1133/aw_client/client.py#L510-L597
[client-disconnect]: https://github.com/ActivityWatch/aw-client/blob/f80900ea38ef9411e2600b633877c7f9922f1133/aw_client/client.py#L424-L431
[rust-commit]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-datastore/src/worker.rs#L330-L388
[window-main]: https://github.com/ActivityWatch/aw-watcher-window/blob/a82b6d13c9fc7c87fb3020184e85676d5ccf70f0/aw_watcher_window/main.py#L95-L142
[window-poll]: https://github.com/ActivityWatch/aw-watcher-window/blob/a82b6d13c9fc7c87fb3020184e85676d5ccf70f0/aw_watcher_window/main.py#L199-L218
[afk-main]: https://github.com/ActivityWatch/aw-watcher-afk/blob/c1a020927028016a7af4041d795fe30d136fe864/aw_watcher_afk/afk.py#L58-L130
[afk-config]: https://github.com/ActivityWatch/aw-watcher-afk/blob/c1a020927028016a7af4041d795fe30d136fe864/aw_watcher_afk/config.py
[input-main]: https://github.com/ActivityWatch/aw-watcher-input/blob/9bb5045456524b215ae11f422b80ec728c93bac7/src/aw_watcher_input/main.py#L26-L62
[rust-device]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-server/src/device_id.rs#L8-L19
[sync-readme]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-sync/README.md#L99-L112
[sync-edits]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-sync/src/sync.rs#L841-L959
[sync-lookback]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-sync/src/sync.rs#L682-L685
[sync-v2]: https://github.com/ActivityWatch/aw-server-rust/blob/e7c54393f6d761c8feb83ea0bd1688fcfe6b3e18/aw-sync/README.md#L74-L76
