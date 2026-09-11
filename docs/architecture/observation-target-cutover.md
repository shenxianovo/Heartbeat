# Observer / Target 切换与存储审阅

[任务 05](../../.scratch/observation-identity-targets/issues/05-cutover-and-cleanup.md) 的当前实施记录。
前四批说明记录各自交付时点；当前业务查询和兼容退出以本文为准。基础模型沿用
[Observations 与 Facts](observations-model.md)，DataSource、Measurement 存储不在本次范围。

## 存储变化

事实仍只有 Segments、Events 两张家族表。ObserverId 与 TargetKind/TargetId 是直接元数据；
Payload 每条仅一份。没有统一 Objects/Facts 副本、Observation 读取流水或窗口登记表。

| 数据 | 当前意义与约束 |
| --- | --- |
| Id | 稳定家族行主键，升级不重分配 |
| OwnerId / StreamId / FactId | 唯一事实身份；不同 Stream 的同 FactId 保持独立，同 Target 不去重 |
| Revision | 原快照更新规则；旧新表示收敛不靠增加 Revision |
| ObserverId | 具体观测者；只有历史保存了可靠身份才回填，null 是明确未知 |
| TargetKind / TargetId | 唯一长期业务归属；device、application-context、account、person 各指向具体资料；成对 null 是未知 |
| AppIdentityId | 保存的平台应用证据；与应用产品、Observer 及传输身份不同 |
| 家族时间 / Payload | 原值保留；不把 Event 当零时长 Segment，不改历史内容适应新术语 |

前四批新增的资料为 ApplicationContexts（Owner/Device/App 唯一）、ServiceAccounts、
ServiceProducts、Persons、PersonAssociations。Target 引用的 Owner/存在性/删除保护沿用已有
数据库触发器与业务外键；本次没有新增目标种类或改写这些约束。

本次唯一追加 migration 是 `20260911004949_CompleteHistoricalTargets`：将剩余 null Target
且旧 Subject 明确为 Machine、有 DeviceId 的家族行补为 device。此前 System/Browser/VRChat
已分别回填，本迁移主要覆盖其他来源的已知设备归属。仅写 TargetKind/TargetId，不改 Observer、
家族身份、Revision、时间、AppIdentityId 或 Payload，不替换物理表。
既有 Target 不改；未知账号/本人不借用当前配置，未知 App 不猜产品，未知 Observer 不补实例。
旧无字段机器输入/旧 segments 导入采用相同映射，迟到缓存不会再次产生依赖查询回退的行。
Down 拒绝有损逆向修改；恢复依赖升级前完整备份。

## 查询切换

ActivitySegments/InputEvents 的 SQL 投影、原始 Fact API、活动 API、Experience、Recap 与
Question 的对象名称都直接消费 Target；业务归属不再读取 Stream.Subject。设备来自 device
或 application-context.DeviceId；账号通过 ServiceAccounts/ServiceProducts 关联 App。
本人仍仅消费明确的 Person Target 与适用时间范围内的使用者关联。Report 仍限 System 互斥轨。

移除 SegmentResponse、ExperienceSegment 的 subjectId/subjectKind/subjectName JSON 别名，
重新生成 OpenAPI 客户端。Dashboard 的 Target 泳道不再将旧 Subject 当对象；未知历史标为
“未知对象”，可以按原家族行读取。unknown:<stream> 只区分未知泳道，不声称恢复对象身份。
App detail 的 system usage DTO 仍有 DeviceId，这是已解析的设备 Target 投影。

## 兼容消费者与退出证据

| 路径 | 决定 / 实际消费者 | 退出条件与验证 |
| --- | --- | --- |
| 业务查询 Stream→Subject 回退 | 删除；回填已知设备后，未知保持 null 并可直接读取 | FactHttpTests.Cutover、家族迁移及完整副本逐行对照；检索 Services/Data 仅保留摄入身份适配 |
| 查询 JSON 的 legacy subject 别名、Dashboard 旧归属推断 | 删除；当前 Dashboard/OpenAPI 已切换 | 未知历史 HTTP 测试、Experience/App detail 测试、重新生成客户端与类型检查 |
| smoke-local-data 的 Subject 回退 | 删除；入口明确要求最新 Target schema | 独立新库 smoke 与当前查询对照；旧版本核对沿历史 rehearsal 脚本 |
| Runtime v1–v5→v6 读取器、旧无字段信封 | 保留；升级前磁盘缓存/已发送但未 ACK 的请求仍可能存在 | 所有实际安装升级且旧缓存/备份恢复窗口关闭；CollectorRuntime、FactHttpTests 的旧缓存→HTTP 重放继续保留 |
| Browser pendingSegments/foldState、旧字段读取、初始化 Subject adapter | 保留；Service Worker 持久队列/会话、安装绑定仍使用当前初始化契约 | 全部 Profile 升级并排空队列，初始化协议消费者一起切换；delivery/recovery、BrowserRuntime_V4CacheUpgrade 测试 |
| VRChat checkpoint v1/v2→v3 | 保留；本地 headless 有 v2 检查点且未完成真实账号升级 | 停止实例后保存检查点，升级后以旧 FactId/Revision 终结/重放，无当前账号补历史；真实升级关闭后才可删旧 reader |
| FactStore 无字段输入 / LegacyImport | 保留；旧 Runtime outbox、segments/input 缓存及其离线恢复 | 已安装版本盘点、旧缓存排空、dead-letter 解决和备份恢复窗口结束；旧新 HTTP 接管/重放不丢失、不双写 |
| ServiceAccounts.LegacySubjectId | 保留为无法恢复服务 ID 的历史账号身份依据 | 属于长期未知历史数据，不以客户端升级为删除门槛；不得冒充服务内账号 ID |
| Stream、Stream.Subject、Source | 保留交付流身份、旧导入接管、Gap/ACK、包输出及管理授权消费者 | 不属于本次业务归属清理；不可机械删除或改名为 Observer/Target |
| activityKey 的旧 identityKey 读取适配 / 活动 DTO 投影 | 保留缓存格式与仍在使用的活动展示接口 | 下游活动 DTO/缓存消费者一起迁移后才移除；本次不扩张为另一轮活动字段重构 |

真实旧版本与离线备份没有全部退出，因此不能删除摄入/缓存兼容。这里的剩余门槛具体对应
01 的 System/Windows、03 的 VRChat 账号以及 05 的实际安装和生产发布验收。

## 验证与审阅

自动验证、完整副本规模/镜像/耗时/资源与失败恢复结果见
[切换 runbook](../runbooks/observation-target-cutover.md) 和任务 05。自动链路包含 System publisher、
Browser Runtime v4、VRChat managed process → Runtime → Analytics HTTP → PostgreSQL → 查询；
Browser headless/本人 UI 的实机范围沿前批记录，不能冒充 Windows 或真实 VRChat 账号验收。

新增风险回归先验证失败：其他历史来源的 Segment/Event 缺少 device Target，以及未知历史仍输出
Subject 别名；修复后再验证通过。旧查询 fixture 现在明确设置 device Target；原来尚未持久保存
Device 的一个 fixture 先保存设备再构造事实，避免把临时 0 行号写成 Target。

本次不改变产品维护对应用上下文引用的事务规则，不关闭未实测的整机资源、实际安装或发布门禁。

发布命令的失败路径改为记录异常并正常返回退出码 1。完整资源演练中，原来未捕获异常虽然已
打印迁移错误，受限 Linux 容器仍持续运行，无法让 CI 及时判定结束；公开命令回归先确认原行为
退出码为 134，修复后 --migrate/--check-database 和 Production 启动校验失败均返回 1，且不开 HTTP。
没有改变成功入口、SQL timeout 或迁移顺序；成功仍返回 0。

### Standards

固定基线 `5c41471885f5803581f06f9b70afd190e4984d46`；独立子代理审阅本次暂存改动，并补审
发布命令退出处理与恢复脚本。0 项确认的规范违反，0 项需本次处理的 smell 建议。

### Spec

同一固定基线，依据任务 05 / PRD 与 ADR-058 独立审阅。0 项确认缺陷；未知历史、身份保全、
旧缓存消费者和失败不启动的实现符合规格。完整数据演练尚未完成，不将脚本存在视作验收。

两轴合计：Standards 0，Spec 0；各轴均无确认的最高风险发现。生命周期保持 ready-for-human，
因 Owner 暂停受限演练、全量比对/恢复及实际安装/资源/发布门禁未关闭。
