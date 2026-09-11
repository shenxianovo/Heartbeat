# 06 — VRChat 账号观测全链路切换

**What to build:** 真实 VRChat 账号位置观测使用独立事实契约，经托管 Collector、SDK/Runtime 和 HTTP 保存并读取；checkpoint、离线恢复和旧事实收尾保留身份，没有设备依据也能工作。

**Blocked by:** [03 — 旧事实与旧缓存无损接管](03-legacy-fact-and-cache-takeover.md).

Status: ready-for-agent

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [ ] 真实 VRChat presence 状态机与托管发布路径使用新契约，贯通 SDK/outbox、Runtime、HTTP、PostgreSQL 与查询；不以旧账号 Subject/Stream 作为新 Fact 存在前提。
- [ ] FOI 为明确账号，Observer 跨正常重启稳定，Aspect 保持 account-location；没有运行设备或 App 证据时关系为空，不用采集宿主或当前登录补历史。
- [ ] 账号位置持续观测保持 Fact Id 并递增 Revision，位置转场及实际身份变化产生相应新事实；增长、轮换及收尾遵守固定属性和家族时间规则。
- [ ] 从 active checkpoint 恢复后正确收尾；即使 End 不变而终态变化，发布版本也可更新。不得将停机期间无观测依据的时间扩充为已有事实。
- [ ] 所有仍支持的 VRChat checkpoint、pending Facts/Gaps 和专有持久状态经真实迁移入口恢复，保留 Id、版本、内容、时间及确认状态。
- [ ] 旧未知账号事实与升级后的明确账号按已确认映射规则区分，旧事实正常结束和重放不被当前账号接管或静默合并。
- [ ] 离线、进程重启、迟到 ACK、重复/乱序和 Gap 通过实际托管链路验证，服务端读回保持准确账号归属和唯一事实。
- [ ] ManagedProcess 协商和旧包回退不会读取不支持的新状态；失败保留可恢复资料，账号授权和运行管理职责保持。
- [ ] 对应 presence/checkpoint、托管协议/Runtime、HTTP 回归通过，记录真实账号安装验收步骤及证据；缺少现场验证时明确承接状态。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。
