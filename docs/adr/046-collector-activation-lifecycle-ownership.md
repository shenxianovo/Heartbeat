---
status: proposed
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

具体 Interface 在 Design It Twice 比较完成前保持 proposed；候选必须按 Depth、Locality 与 seam placement
评估，并以 replace-not-layer 迁移删除旧 coordinator 和调度测试。

