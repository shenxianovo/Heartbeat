# 01 — 独立观测保存与读取

**What to build:** 完整的新 Segment/Event 不提供旧 Subject/Stream，也能经真实 HTTP 保存、重传、正常修订并从公开查询读取。生产者的稳定 Id 是新事实的存储身份；持久化和验证由观测契约决定。

**Blocked by:** None — can start immediately.

Status: ready-for-agent

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [ ] 先用真实 HTTP、PostgreSQL 和查询 fixture 稳定复现：无旧 Subject/Stream、无关系的完整观测当前不能独立存取；记录失败原因，不能在测试或入口内伪造 Stream。
- [ ] 新原生 Segment/Event 自身声明 Id、Kind、Collector、FOI、Aspect、Result、Time、Revision，Owner 取认证身份。读回 Id 与生产者 Id 相同，空 Relations 合法，未知 Aspect 和未知 JSON 字段完整保管。
- [ ] 同步扩展请求/响应、实体、数据库约束及原始查询，旧交付键对新事实不再必需；机器、App、账号、个人按各自合法身份引用可独立存取，不要求补造设备关系。
- [ ] 新原生输入缺少有效 Collector/FOI/Aspect 时明确拒绝；历史 null 的存储能力不会放宽原生验证。对象解析使用真实身份及既有产品规则。
- [ ] 同一新 Fact 的 Observer、实际 FOI、Kind、Aspect 固定，更高 Revision 也不能更换；Segment 起点和 Event 发生时刻固定。App 目录身份解析与维护沿父规格的独立规则处理。
- [ ] 同版本相同完整语义幂等、不同内容冲突，低版本不覆盖高版本；合法高版本可更新 Result、延长或缩短 Segment 及更新关系，保持 Id。
- [ ] 完整 Fact 与其关系原子提交，失败不留下部分事实或成员；关系绑定准确 Fact，角色/种类/基数与 Owner 有保护，无关系事实也能读取。
- [ ] Collector、私有对象、关系引用与查询保持 Owner 隔离；独立事实 Id 碰撞不覆盖已有行，跨 Owner 响应不泄露他人内容。
- [ ] 新旧入口收敛到同一事实写入核心和唯一 Facts；现有生产者、旧行身份与旧入口在扩展阶段仍可使用。追加迁移保住已有数据，旧身份/缓存的完整升级矩阵由 03 承接。
- [ ] 原始读取完整支持新事实；领域对象维护由 07、派生分析和 Dashboard 全面切换由 08 承接，不能把这些后续范围提前声明完成。
- [ ] 对应 HTTP、存储和迁移回归通过，记录可重复命令、结果与未验证范围；本任务不操作业务库或恢复暂停的生产演练。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。
