# 02 — 原生观测的持久保管与交付

**What to build:** Collector 使用公开 SDK/协议发布独立 Fact，Runtime 在离线和重启中持久保管，通过真实 HTTP 写入 PostgreSQL 并可查询；上传确认只确认准确发送的快照。

**Blocked by:** [01 — 独立观测保存与读取](01-independent-observation-custody.md).

Status: ready-for-agent

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [ ] 经公开 SDK/Collector Protocol 的最小发布用例贯通 Runtime、HTTP、PostgreSQL、读取与 ACK；新事实不靠生成旧 Subject/Stream 才能提交。优先复用现有 transcript/HTTP fixture。
- [ ] 扩展共享契约及受支持执行方式的发布/初始化接口，使事实自身携带身份、Kind 与观测信息；保留尚未迁移调用方的可用形式，各批迁移可分别保持回归通过。
- [ ] Runtime 与服务端一致验证原生完整性及同 Fact 固定 Observer/FOI/Kind/Aspect；保留家族时间、完整 Result、Relations 与单调 Revision，不改造成旧活动投影再交付。
- [ ] SDK/outbox 与 Runtime 在停止、重启、重试后保留 Fact Id、版本、内容、终态和待发状态；新格式可持久恢复，测试观察真实重启后的发布或待发结果。
- [ ] 上传期间产生更高版本，再收到旧 ACK 时，新版本仍待上传；同一发送版本也必须核对完整快照。最终确认后再次重启，不重复保留已确认版本或丢失更新版本。
- [ ] 乱序、重复及同版本冲突在 SDK/Runtime/服务端交互中正确处理，失败不会被误确认；正常 Segment 增长、缩短和收尾遵守既有语义。
- [ ] Gap 的实际丢失范围、身份及确认保持，重启重放不增加或遗失 Gap；交付需要的分组由交付自身保管，不决定 Fact 的 FOI、Kind 或身份。
- [ ] 协议协商、缓存版本及包启动/回退保护同步扩展，不理解新契约的包不能处理新缓存或静默 ACK 新事实；失败保留可恢复状态。
- [ ] 本项落实通用新链路和兼容入口的明确接口；完整旧数据升级/接管由 03、三个真实生产者与专有状态迁移由 04–06 验收，不复制永久双写链路。
- [ ] 对应协议、outbox、Runtime、HTTP 集成回归通过并记录证据；覆盖实际受支持的执行方式，无法运行的验证明确承接，不将示例发布当成第一方生产者已完成切换。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。
