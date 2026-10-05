# ADR-0027：Collector 的待交接缓冲由 Hub Client 共用

## 状态：已接受

## 日期：2026-09-28

用户确认将桌面已有的待交接缓冲移入 `Heartbeat.Hub.Client`，供桌面和 VRChat Collector 共用。桌面实现已经处理声明分组、条数与字节分批、发送快照确认和并发续期；VRChat 自行按条数分批，缺少字节边界。继续分别实现会让新 Collector 重复维护同一交接契约。

`Heartbeat.Hub.Client` 提供 `PendingHubSubmissions`：按 Record ID 暂存最新快照，按 Collector 与 Track 声明生成提交批次，只有调用者确认成功提交的快照后才释放它。发送期间暂存的新快照不会被旧批次的确认清除。条数和字节上限定义在 `HubSubmissionLimits`，由分批工具与 Hub 接管端共同引用。

Collector 仍决定观测投影、稳定 Record ID、连续性、调用节奏、重试与停止时的最终交接。缓冲位于 Collector 进程内，不负责调度，也不提供落盘、容量丢弃或崩溃恢复。Hub 的 SQLite 事务仍是持久接管边界；`SubmitAsync` 成功只证明接管，不证明后端已写入。

这细化 [ADR-0009](ADR-0009-delivery-belongs-to-hub.md)，并调整 [ADR-0015](ADR-0015-shared-desktop-observation-pipeline.md) 中待交接缓冲的代码归属，桌面观测管线的责任不变。不引入新的协议版本、历史结果更正或兼容实现。接管前的数据保护与积压策略仍见[未决问题](../recording-open-questions.md)。

接入方法见 [Hub Client](../../src/Hub/Heartbeat.Hub.Client/README.md)。共享交接测试覆盖路由、条数与字节边界、SQLite 拒绝接管后的保留与重试、旧回执与续期；真实 VRChat 事件仍按既有人工验收处理。
