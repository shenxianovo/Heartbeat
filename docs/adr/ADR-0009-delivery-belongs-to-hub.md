# ADR-0009：交付归 Hub，采集只管观测

## 状态：已接受（事后追认）

## 日期：2026-09-17

[`2d86169`](https://github.com/shenxianovo/heartbeat/commit/2d86169c4699bbe1c4ddff3232ea68b4736fa114) — feat(hub): own collector registration and durable record delivery

决定在 2026-09-12 随实现落地，本 ADR 补记责任划分及理由。

## 背景

最初由 Collector 直接注册后端身份、上传和重试，连续性状态还进入后端 Application 层。这使离线时无法首次接入，每个平台都要重复交付逻辑，后端也被迫理解采集语义。

考虑过两个替代方案，都否掉了：

- Collector 各自维护本地队列仍会复制重试、幂等和映射。
- 后端缓冲无法解决后端离线时的本地接管，也不掌握本机生命周期和容量。

## 决策

在 Collector 与后端之间放一个本机组件 Hub，由它拥有交付。分工固定成四句话：**采集 Collector 做，交付 Hub 做，存储后端做，展示前端做。**

- Collector 只提交逻辑声明（collector key/target、Track 的协议标识）和 Record 内容，不认识后端 ID。
- Hub 用 SQLite 持久接管，收下就落盘；再解析后端身份、上传、重试、逐条核对回执。
- 后端只管 Owner 归属、公共记录不变量和存储查询，不理解任何采集语义。`IContinuousStateStore` 因此被删掉。

## 后果

- 首次接入不需要后端 ID，后端离线时 Hub 仍可接管。
- 重试、幂等和后端映射只有一处实现；Hub 可在 Collector 断开后继续交付。
- 系统多了一个必须运行的本机组件和一份本机状态。
- Hub 接管前的数据仍由 Collector 内存保管，进程退出可能丢失。
- 永久失败记录会暂停并占用容量，当前没有人工重试或删除入口。
- 接收与投递仍在同一进程，进程级故障会同时影响两者。

## 参考

- [`docs/hub-record-delivery.md`](../hub-record-delivery.md) — 交付链路、提交入口与回执语义
- [`src/Hub/Heartbeat.Hub/RecordOutbox.cs`](../../src/Hub/Heartbeat.Hub/RecordOutbox.cs) — SQLite 接管与状态机
- [`src/Hub/Heartbeat.Hub/RecordUploader.cs`](../../src/Hub/Heartbeat.Hub/RecordUploader.cs) — 身份解析、上传与回执处理
- [`ADR-0002`](ADR-0002-monotonic-record-extension.md) — Range Record 只延长不改值，是 Hub 幂等提交的前提
