---
status: accepted
---

# ADR-046: Collector Activation 两侧各自拥有唯一生命周期事务

Collector Activation 的 Hub 与 Collector 进程各自建立一个唯一生命周期 owner，而不创建跨进程巨无霸。
Hub owner 接受 Starting/Ready/Stop Intent 并持有不会因 caller 取消、异常观察或重试而替换的 terminal
task/result；Runtime Dispose、activation cleanup、update、deactivate 与 supervision 不再直接执行 Stop。
Collector Protocol Client owner 以显式 delivery ownership 完成 background → drain → fenced 转移，并在一个
线性化 transition 中关闭 admission、推进 commit epoch、固定绝对 deadline。这样把 Stop failure/retry、
deadline、writer release、truthful remainder 与 completion error 集中到各自深 Module，同时保留 ADR-040
的 Package / Instance / Activation、单一 Collector Protocol、多 Transport Binding，以及 InProcess、
ManagedProcess、ExternalHost 的真实能力差异。Artifact Delivery 与 Registry 不进入本决策。

Design It Twice 比较了最小 Interface、最强 typed state 与最易迁移三案；采用混合设计：Hub 的
`CollectorActivationLifetime` 在 accepted Hello/reservation 时创建，只暴露 Ready publication、Stop Intent
与持久 Terminal；Client 的 `CollectorDeliveryOwnership` 用 ordinary/drain-tail admission 与 background/drain
lease 表达 ownership。InProcess 的有限 cooperative retry、ManagedProcess terminate 与 ExternalHost lease
revoke 都由 Module/Driver Adapter 内部决定，caller 不通过 task reset 重试。

Client drain 开始时一次线性化关闭 ordinary admission、supersede background lease、推进 commit epoch、
转移 delivery ownership 并捕获 Hub 的绝对 deadline。Application Stop 只收到 drain-scoped context 来提交
已截断 ingress tail；Fact/Gap commit 明确返回 Superseded/Fenced，而不借用 cancellation 异常。迁移必须
replace-not-layer：旧 StartingCollector、Activation stop-task coordinators、direct Runtime Stop paths、client
handoff bool/observer 与调度测试在对应 slice 中删除。
