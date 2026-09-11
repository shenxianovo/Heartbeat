# 05 — Browser 多窗口全链路切换

**What to build:** 真实 Browser 多窗口活动通过新观测契约发布、持久保管和读取；快照使用可恢复的单调 Revision，旧扩展缓存升级、Service Worker 重启和迟到 ACK 均保持独立事实。

**Blocked by:** [03 — 旧事实与旧缓存无损接管](03-legacy-fact-and-cache-takeover.md).

Status: ready-for-agent

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [ ] 实际 Browser fold/发布/交付经 ExternalHost、Runtime、HTTP 到 PostgreSQL 与读取使用新契约，不在适配处虚构旧 Subject/Stream 使新事实成立。
- [ ] 持久扩展安装身份继续表示具体 Observer；FOI 为 App 产品，设备关联来自明确 observed-on 证据，不以 Runtime 实例或宿主猜测归属。
- [ ] 多窗口同时访问相同 App/页面仍保持各自 Fact Id；窗口转场、关闭、重开及活跃快照增长遵守现有活动连续性规则，保留必要窗口证据但不将窗口登记为新的持久 FOI。
- [ ] 新快照维护持久化单调 Revision，EndTime 相同而完整发布内容变化、合法结束时间缩短及收尾都可得到更高版本；重传同一快照保持原版本，不以 EndTime 计算新旧。
- [ ] 旧 endTime 派生版本及旧 pending/fold/dead-letter 状态迁移保留原 Id、版本高水位、完整内容和待发责任；转换本身不增加版本，新变化的版本高于已保管旧版本。
- [ ] 新旧队列并存、Service Worker 重启、离线重试与升级恢复不重复产生事实；同版本冲突保留可恢复依据，不能静默覆盖其中一份。
- [ ] 迟到 ACK 对应准确发送版本与完整快照，不能删除已增长或更新的当前快照；Gap 和包/协议能力保护在新链路仍正确。
- [ ] 用真实 Browser 生产逻辑贯通发布与服务端读回，并以 UI/运行 fixture 验证实际多窗口行为；仅手写原生 HTTP 数据不能完成本项。
- [ ] 对应 Browser、ExternalHost/Runtime、缓存和 HTTP 回归通过，明确真实 Profile/浏览器验收步骤、证据或待承接状态；不恢复暂停的生产部署。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。
