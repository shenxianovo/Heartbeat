# 03 — 让 InProcess drain 受 deadline 约束

Status: ready-for-agent

Owner: Collection / Collector Protocol

Priority: P1 — 应用 Stop 若不返回，Runtime 永远无法报告 remaining facts/gaps 或完成宿主退出。

## What to build

让 drain request 的 deadline 覆盖 application stop、adapter pump flush 和 drain completion 整个过程。
InProcess System adapter 与通用 client 使用同一有界语义：到期后保存可恢复状态并返回真实 remainder，
不得以 `CancellationToken.None` 进入无界 stop/retry。

## Acceptance

- [ ] 收到 drain 后立即根据绝对 deadline 创建 token；`application.StopAsync`、pump flush、outbox flush
  与 completion 都受该 deadline 或更短局部预算约束。
- [ ] cooperative stop 正常完成；hung/ignoring-cancellation application 到期后 Runtime 仍在有界时间返回，
  并报告 durable pending fact/gap counts。
- [ ] deadline 过去、stop 抛错、flush 取消、binding completion 失败各有稳定 runtime reason；不得宣称
  fully drained。
- [ ] InProcess adapter 不存在 deadline 外无限 retry；宿主退出后 restart 能重放 durable remainder。
- [ ] fake-clock/controlled-task tests 覆盖 stop-before-deadline、stop-at-deadline、never-stop、pending facts/
  gaps、completion failure 与 restart replay，且不会留下后台 task/双 writer。
- [ ] ManagedProcess/ExternalHost 现有 drain transcript 不回归；共用 conformance fixture 对三种 driver 的
  deadline/result 断言一致。
