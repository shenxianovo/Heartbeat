# 03 — browser Collector 切换到 ExternalHost Binding

**What to build:** 让真实浏览器扩展作为 ExternalHost Collector 完成 Activation、配置收敛、Stream 开启和 Segment ACK，并在浏览器退出或长期不续租后诚实结束 Activation，同时保留现有本地队列与 Collector Active 体验。

**Blocked by:** 01 — 本地参考 Package 跑通 Collector Protocol.

**Status:** ready-for-agent

- [ ] 浏览器扩展通过 loopback ExternalHost Binding 协商协议、取得完整 Spec、打开 browser Segment Stream，并只删除已明确 ACK 的 outbox Fact。
- [ ] ExternalHost 使用有 ACK 的会话租约；浏览器关闭或停止续租后 Activation 在有界时间内离开 Ready，Runtime 不虚假声称自己终止了浏览器。
- [ ] Desired Enabled 仍由 Hub 持有；停用后 Hub 拒绝新 Fact，扩展也停止采集或发布，但临时断连不改写 Desired State。
- [ ] AppHint 在 wire Fact 之外形成独立 enrichment 或由 legacy adapter 处理，原始 FactSubmission 的 canonical 内容不被 Hub 改写。
- [ ] Observation Depth 声明由 Collector Package 持有、在运行时注册，reading 使用相对 typed payload 的稳定取值路径；现有 browser site→url→title 投影与 Matcher 继续工作。
- [ ] 旧扩展缓存和旧 loopback 请求有明确兼容路径；升级期间不会因协议切换静默清空队列。
