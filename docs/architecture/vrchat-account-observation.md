# VRChat 账号观测与事实归属

[Ticket 03](../../.scratch/observation-identity-targets/issues/03-vrchat-account-observation.md)
接续 [System](system-observation-targets.md) 和 [Browser](browser-observation-targets.md) 公共契约。
采用当前 Observations 基线，不引入 DataSource，不改变事实唯一身份或家族存储布局。

## 账号与产品存储

追加迁移 `20260910134705_VRChatServiceAccounts`，已部署迁移保持原样。

| 资料 | 列与约束 |
| --- | --- |
| ServiceAccounts | Id bigint identity 主键；OwnerId；ServiceKey；ServiceAccountId varchar(256) nullable；LegacySubjectId uuid nullable |
| 已知账号 | `(OwnerId, ServiceKey, ServiceAccountId)` 唯一；服务内 ID 与 LegacySubjectId 恰有一个非空 |
| 未知历史 | `(OwnerId, ServiceKey, LegacySubjectId)` 唯一；保留原账号 Subject 身份，不把其 UUID 当 VRChat ID |
| ServiceProducts | ServiceKey varchar(64) 主键，AppId → Apps.Id 的 Restrict 外键；账号 ServiceKey 外键引用该关联 |

服务键为 `vrchat`，既有 Collector Source 仍为 `vrchat.account`，两者不混用。
服务明确对应产品键 `vrchat`；有历史来源的升级或首次账号摄入建立关联，产品存在则复用，
没有则创建产品，不创建平台 AppIdentity。账号解析复用 FactStore 的 Owner/Catalog 事务锁，
唯一索引承担最终并发保护。不同 Owner 的同一服务账号有各自资料行。

产品显式 Merge 更新 ServiceProducts.AppId，再删除源产品。仅移动平台身份的 Catalog/Override
纠错保留仍被服务引用的产品、图标和产品别名；Catalog 隐式选产品也不能因平台映射改变而重命名服务产品。Facts 的 Target、Observer、Revision、时间和 Payload 不随产品合并改写。
家族表继续保存 `ObserverId/TargetKind/TargetId` 和一份 Payload，不复制事实、不建统一对象表。
Target 检查扩充 02 的 PostgreSQL 触发器，要求同 Owner 的账号存在并取得行共享锁；
删除被引用账号、改变其行 ID、跨 Owner 与悬空引用被拒绝，账号的 Owner/服务/身份字段不可原地改写。
使用者关联属于 04；Owner 所有权不证明本人在全部历史中使用该账号。

## 离线引用与采集

离线引用示例（测试身份）：

```json
{"kind":"account","reference":"[\"vrchat\",\"usr_11111111-1111-4111-8111-111111111111\"]"}
```

新引用接受 VRChat 的 `usr_` 加小写 UUID 格式，拒绝显示名、裸 UUID、Collector UUID 和空白。
Analytics 在认证 Owner 内解析行号，无需在线登记后开始活动。多个 Observer 可以共用账号 Target，
事实仍按 `(OwnerId, StreamId, FactId)` 独立保存，不按 Target 去重。

`VRChatPresence.ObservedAccountId` 来自每次 presence 响应。当前 adapter 读取
`CurrentUser.Presence`，同时取**同一次 CurrentUser 响应**的 Id，因为此接口实际描述该用户。
授权接口的 DisplayName、cookies、用户名/密码均不参与观测账号身份。未来若读取其他账号，
必须使用那次被观测响应的 ID，不能复用登录身份。

Observer 使用 Runtime 提供的持久 CollectorInstanceId；账号切换、Activation 和世界变化不改变它。
PresenceStateMachine 判断账号、世界、实例连续性；PresenceFactPublisher 承担 FactId、Revision、
切片、终态与恢复机械处理。没有扩展通用 SDK。账号切换即使世界/实例相同也结束旧事实、开启新事实。
世界名更新不改变连续性；恢复仍在保存的 End 终结旧事实，停机区间记 Gap，不延长成虚构观测。
新观测必须有服务账号 ID。新业务字段和发布 Payload 使用 ActivityKey/activityKey；账号身份走 Target，
世界/实例细节仍在 Payload。实例旧 Subject 只服务流身份兼容，不覆盖逐条 Target，也不代表宿主设备。

## 历史、缓存和检查点

| 边界 | 映射与保全 |
| --- | --- |
| 已部署账号事实 | 旧 VRChat 输出/Stream 未保存服务内账号 ID；按 Owner/服务/Subject 建明确未知的历史账号。native Stream 有 CollectorInstanceId 则恢复 Observer，legacy-import Observer 保持 null |
| 迁移保全 | 表 OID、行 Id、Stream、FactId、Revision、时间、Payload 不改；重复升级不增加事实 |
| checkpoint v1/v2 → v3 | 保留 `.v1.bak`/`.v2.bak`；读取适配器将 IdentityKey 改为 ActivityKey，保留原 FactId/Revision/Start/End、active、pending facts/gaps，旧 ObservedAccountId 为 null |
| checkpoint v3 | 保存 ActivityKey 与新事实的 ObservedAccountId；沿用 Stage/ACK 原子替换，恢复旧 active 按原 End 终结，不用当前登录重新绑定 |
| Runtime v1–v5 → v6 | 沿用完整备份、验证、原子替换；Segment 活动 identityKey 规范为 activityKey，原事实/流身份、Revision、时间、Delivered、Gap 保留，不猜旧账号 |
| 旧协议/outbox | 保留旧 null Observer/Target 形状；Runtime 同 Revision 比较前和 Analytics 入库前共享活动字段规范化。旧 VRChat Account Stream 仅解析未知历史账号 |
| 更早 segments HTTP 缓存 | `subject:account:<UUID>` 旧入口首次导入即解析未知账号；重试及原生接管保留同一行，不等待日后重放补齐归属 |
| 新快照 | 显式 Observer/account Target 经 stdio、Runtime 保管与 HTTP ACK；完整元数据参与确认比较 |

旧数据确实没有服务 ID，不能把当前登录或宿主补为过去身份。新版本已有 Target/ObservedAccountId
的快照原样保留；未知账号也可直接按 accountId 查询，不需要在线 Collector 或 Stream→Subject 查询回退。
旧形状与升级形状同 Revision 重放收敛到同一事实，不提高 Revision 回避冲突。

## 查询与展示

Segment/Event HTTP 查询增加 `accountId`，支持与 `appId/start/end` 联合过滤，沿用 Owner 可见性门。
TargetId 指向 ServiceAccounts.Id；设备维度为空。活动和当天经历通过明确服务关联投影 App 和 TargetName，
已知账号显示服务 ID，旧账号标为“历史账号（身份未知）”。现有 Target 泳道与筛选可回看账号，
不同 Observer 的事实独立保留。Report 和 System 应用时长仍只读 System 设备活动；
账号服务关联不意味着机器上运行过该 App。OpenAPI 客户端同步 accountId 参数。

## 兼容退出与验收

兼容对象仅为改造前第一方 Collector/Runtime 版本及其 outbox、checkpoint、Runtime JSON 和已部署事实。
05 移除读取适配器与旧无字段输入入口的前提：三个 Collector 已迁移、旧版本退出、缓存排空或已验收升级重放、
可映射历史完成回填且未知历史直接可查、离线与回滚窗口明确。未知历史账号不能随 adapter 删除而删除，
其未知语义是存量信息限制，不是新观测缺账号的长期入口。
旧协议 ActivitySegmentItem.IdentityKey 投影别名继续服务管理/展示消费者，退出门槛同上；
VRChat 新业务字段和发布代码使用 ActivityKey。旧 Subject 的传输身份职责不能机械改名为 Observer。
Down 拒绝有损回滚，恢复使用升级前备份；沿用 ADR-055 停写窗口。本任务仅迁移隔离测试库，未部署生产。

真实验收：通过现有 Hub 本地管理授权页配置新制品实例，不把凭据写入终端。账号进入可观测世界，
等待至少两次 poll，核对同次服务响应账号 ID 与 TargetName/账号查询；停止后仍可回看，重启 Observer/Target
保持，切换账号后新旧事实分开。真实授权或 presence 不足时保留 ready-for-human。
2026-09-10 检查本地旧 headless 实例存在、checkpoint 为 v2；collector-secrets 目录只有加密密钥，
没有可复用会话。未进行真实账号验收，未请求或记录用户凭据。
自动验收使用可控会话，独立 Collector 进程经 Runtime → Analytics HTTP → PostgreSQL → 查询，
覆盖授权、停止/重启、待发重放；具体回归和 review 记录见 Ticket 03。
01 的真实 System/Windows 门禁仍保留，02 完成状态不变，05 仍需生产副本演练与清理。

Runtime 新增兼容处理按 Segment 家族与既有活动形状执行，与 Analytics 的 ActivityFactPayload 规范化一致；
Event Payload 不受本次新增转换影响。没有新增具名 VRChat 的 Host 分支，保持 ADR-049 的组合边界。
旧 segments 入口保留的 `subject:account:` 设备标记只是旧传输适配资料；事实直接归属账号，设备查询不纳入它。
