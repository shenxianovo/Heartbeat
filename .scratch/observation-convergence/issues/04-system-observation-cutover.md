# 04 — System 活动与输入全链路切换

**What to build:** 真实 System Collector 的桌面活动和输入事件使用独立观测契约，从生产、持久保管、上传到读取完整运行，升级及重启不会丢失进行中活动或待发输入。

**Blocked by:** [03 — 旧事实与旧缓存无损接管](03-legacy-fact-and-cache-takeover.md).

Status: ready-for-agent

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [ ] 真实 System 活动与输入生产者经 SDK/Runtime/HTTP 写入 PostgreSQL 并读取；使用新事实自身的 Id、Kind 和完整观测信息，旧 Subject/Stream 仅在仍需管理/兼容的职责中存在。
- [ ] System FOI 保持机器，具体 Observer 身份跨重启稳定；desktop-activity 与 input 语义明确，App 关系只来自实际观测，无 App 的桌面状态也完整保存。
- [ ] 同一活动增长或收尾保留 Fact Id，变化快照递增 Revision；实际转场产生新事实，固定身份属性与家族时间规则一致，既有标题/away/恢复及区间轮换行为保持。
- [ ] 输入 Event 的发生时刻、Id 和完整编码结果保留；重传不重复存储，未变化的事件可保持 Revision 1。
- [ ] 活跃 checkpoint、旧活动/input 缓存及待发记录经真实升级/恢复入口接管，保留原身份和版本；恢复不延伸没有观测依据的历史区间。
- [ ] 离线、重启、持续活动期间的迟到 ACK 及 Gap 保管通过完整链路验证，旧确认不能清除当前新快照。
- [ ] Windows/macOS 的受支持 System 路径均纳入切换与依赖盘点；可运行的平台测试通过，不可运行部分提供可重复步骤和明确承接者。
- [ ] 本任务的读回验证使用实际观测结果与关系；全量分析/Dashboard 行为由 08 承接，不能靠仅构造 HTTP 数据声明 System 已切换。
- [ ] 对应 System、协议/Runtime 和 HTTP 集成回归通过，记录自动与实际安装验证的区别；仍需人工门禁时按仓库规则保留待验收状态。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。
