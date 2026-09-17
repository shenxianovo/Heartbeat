# ADR-0009：交付归 Hub，采集只管观测

## 状态：已接受（事后追认）

## 日期：2026-09-17

[`2d86169`](https://github.com/shenxianovo/heartbeat/commit/2d86169c4699bbe1c4ddff3232ea68b4736fa114) — feat(hub): own collector registration and durable record delivery

决定本身在 2026-09-12 随实现落地，当时只留了 `docs/hub-record-delivery.md`，没有写 ADR。这份是补记，用来解释「为什么交付这件事不放在 Collector 也不放在后端」。

## 背景

最初的写法是 Collector 直接对后端说话。`Heartbeat.Collector.Desktop.Mac/HeartbeatRecordingClient.cs`（128 行）一个人干了四件事：注册 Collector、把观测映射成后端协议、上传 Record、失败重试。连续性状态还住在后端的 Application 层（`IContinuousStateStore`）。

这套写法有三处硌人：

- **首次接入必须先拿到后端 ID。** 后端不在线，采集就起不来。可采集这件事本来和后端在不在线没关系——观测发生在本机，丢了就再也补不回来。
- **每加一个平台，就要复刻一份交付逻辑。** 重试、幂等、ID 映射这些与平台无关的东西，会在每个 Collector 里各写一遍。这是「同一契约多处权威」的标准前兆。
- **后端被迫理解采集侧的概念。** 「观测是否连续」是采集判断，`IContinuousStateStore` 却让存储层持有它。后端本该只认公共记录不变量。

考虑过两个替代方案，都否掉了：

**让 Collector 继续直连后端，各自加一个本地重试队列。** 它能解决离线采集，但不解决复刻——重试、幂等、映射还是每个平台一份。而且后端仍然要理解采集连续性。省下的是一个组件，付出的是每个平台一份可漂移的实现。

**让后端提供离线接收缓冲。** 这等于把「接管」这个责任交给一个既不掌握设备生命周期、也不知道本机磁盘还剩多少的组件。更根本的问题是它自相矛盾：后端不在线的时候，后端的缓冲也不在。

## 决策

在 Collector 与后端之间放一个本机组件 Hub，由它拥有交付。分工固定成四句话：**采集 Collector 做，交付 Hub 做，存储后端做，展示前端做。**

具体是：

- Collector 只提交逻辑声明（collector key/target、Track 的协议标识）和 Record 内容，不认识后端 ID。
- Hub 用 SQLite 持久接管，收下就落盘；再解析后端身份、上传、重试、逐条核对回执。
- 后端只管 Owner 归属、公共记录不变量和存储查询，不理解任何采集语义。`IContinuousStateStore` 因此被删掉。

## 后果

- ✅ 首次接入不需要预取后端 ID，后端离线也能继续采集，观测不会因为网络而丢。
- ✅ 重试与幂等只有一处实现。新增平台 Collector 只写观测。
- ✅ 没有 Collector 连着的时候，Hub 也能自己恢复积压。
- ⚠️ 多了一个必须活着的本机组件和一份本机状态。Hub 自己的可靠性成了新的单点，而这块目前有实打实的缺口：正常停机没有 flush、映射阶段的永久错误会无限重试、队列容量与输入事件的规模不匹配。见 `.scratch/implementation-review/candidates.md` 的 `C-01`、`C-02`、`C-04`。
- ⚠️ 接收与投递同进程。投递侧的未预期异常会把接收面一起拖走（`C-09`）。
- ⚠️ 「逻辑声明 → 后端身份」这层映射本身成了新的契约面。映射失败要分成「可重试」还是「永久」，这个分类现在依赖后端 400 响应里的 `code`，而 `code` 还不统一（`C-02`、`S-11`）。

## 参考

- [`docs/hub-record-delivery.md`](../hub-record-delivery.md) — 交付链路、提交入口与回执语义
- [`src/Hub/Heartbeat.Hub/RecordOutbox.cs`](../../src/Hub/Heartbeat.Hub/RecordOutbox.cs) — SQLite 接管与状态机
- [`src/Hub/Heartbeat.Hub/RecordUploader.cs`](../../src/Hub/Heartbeat.Hub/RecordUploader.cs) — 身份解析、上传与回执处理
- [`ADR-0002`](ADR-0002-monotonic-record-extension.md) — Range Record 只延长不改值，是 Hub 幂等提交的前提
