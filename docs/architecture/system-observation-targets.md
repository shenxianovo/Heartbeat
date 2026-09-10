# System 事实的 Observer 与 Target

本文件记录 [任务 01](../../.scratch/observation-identity-targets/issues/01-system-observer-target.md)
的局部实施选择，沿用 [Observations 与 Facts](observations-model.md) 和 ADR-056；不改变活动判断、
Fact 身份或家族语义。Browser、VRChat 与本人关联仍按各自后续任务实施。

## 公共契约与引用

`CollectorFact`、`BoundCollectorFact`、Runtime `FactSubmission`、Analytics `FactSnapshot`
增加 `ObserverId: Guid?` 与 `Target: FactTarget?`。`FactTarget` 是 `{ kind, reference }`。
新 System 发布二者必填；nullable 只为改造前第一方输入和历史缓存兼容，不表示新生产者可以省略。
缺一个字段、空 Observer、空引用或当前不支持的 Target kind 都拒绝。
本批仅支持 `device`，没有预建账号、应用上下文、Measurement、Objects 或 Aspect 注册层。

System Observer 直接使用 Runtime 持久 CollectorInstanceId。实例 UUID 随运行目录持久保存，
与 Activation、PID、包版本无关；重启、停用后继续和包更新不重分配。两个实例即便观察同一设备，
也有各自的 Observer 和 Stream。完整复制实例数据目录会复制该身份，复制不等于创建独立实例。

设备引用使用现有 HardwareId，System 将 Machine Subject UUID 编码为小写连字符 UUID（`D`）。
不在线领取行号，不用设备名或当前连接状态作身份。Analytics 在认证 Owner 内按硬件标识解析
既有 Device；UUID 的大小写等价，非 UUID 的旧设备引用保留原样。事实的 Target 由本条发布输入决定，
并不要求等于 Stream 的旧 Subject。一个 Observer/Stream 可发布归属不同设备的事实。

家族表追加 `ObserverId uuid NULL`、`TargetKind varchar(32) NULL`、`TargetId bigint NULL`，
后两个字段是单个 Target 的种类与业务行引用，本批指向 Owner 下的 Devices.Id。
不建立统一目标表；未来种类通过各自业务资料解析。上传引用和数据库业务行号刻意分开，
后者不会作为离线采集前置条件。事实仍以 `(OwnerId, StreamId, FactId)` 唯一，保留原 `Id` 与 Revision。
同 Revision 的 Observer、解析后的 Target、家族时间或 Payload 不同都会冲突；低 Revision 不覆盖高版本。
ObservedAt/IsFinal 仍只在原有协议和 Runtime 承担责任，不新增 Analytics 存储列。

## 承载与确认

字段经过 System publishing adapter、Collector Client outbox、InProcess adapter，以及共享 ManagedProcess
stdio writer/reader。Managed/ExternalHost Runtime wire reader 识别新字段，旧 Browser/VRChat 仍发送旧形状。
JSON canonical request comparison 和消息尺寸计算包含新字段；Runtime 重复 Revision 比较及 HTTP ACK
确认也包含新字段。旧 ACK 不能因 FactId/Revision 相同而确认不同 Target 或 Observer 的快照。

System ingress journal 和活动 checkpoint 保存的仍是 System 自己的业务快照；交付时从持久实例补入
Observer 和设备引用，无需改动 SystemActivityModel。旧 Collector Client outbox 保留原字段和原
messageId/FactId/Revision，经兼容 Runtime 接收；重发不分配新事实身份。新的 outbox 自动序列化新增字段。

## 缓存与数据库升级

Runtime JSON schema 由 3 升为 4。加载 v1–v3 时只为已记录为 System/Machine 的 Facts 补入
CollectorInstanceId 与 Subject UUID；Browser、账号和其他未知数据不推断 Observer/Target。
先完整解析验证、保存 `.vN.bak`，再使用现有 fsync、重读验证和原子 rename 保存 v4；失败保留原文件。
Delivered、Gap、FactId、Revision、时间和 Payload 均保留。旧 System outbox 在接收时采用相同依据，
因此已升级缓存与等价旧形状的同 Revision 重放可以收敛。

`20260910121906_ObservationTargets` 是追加迁移，前置为已部署
`20260908141403_NativeFactCustody`。原有迁移文件不变，原有固定列数测试明确停在对应历史阶段。
新增迁移只增加元数据和索引并回填 System/Machine：设备来源于已保存的 Subject.DeviceId；
只有 native Stream 中已保存的 CollectorInstanceId 才回填 Observer；legacy-import 的 Observer 保持 null。
不改表 OID、行 Id、FactId、Revision、时间或 Payload，不复制事实。

Down 明确拒绝丢失归属的逆变换。生产切换沿用 ADR-055：升级前完整备份、最多 10 分钟停写/停服、
Collector 保管待发缓存、迁移后重放并核对事实；失败用升级前备份恢复并保管新增事实。
本任务不部署，也不以合成迁移 fixture 代替任务 05 的完整生产副本演练。

## 查询与兼容退出

新增公开查询 `GET /api/v1/users/{username}/facts/segments` 与 `/facts/events`，支持 `deviceId/start/end`，
使用原有用户可见性门。返回行 Id、StreamId、FactId、Revision、ObserverId、TargetKind/TargetId、
Source、家族时间和完整 Payload（最多 10,000 行）。窗口对 Segment 使用重叠范围，对 Event 使用半开区间。
现有活动接口和当天经历接口也返回 ObserverId、TargetKind、TargetId、TargetName；System 已有明确 Target
的响应不再输出 subjectId/subjectKind/subjectName。C# 的旧字段明确命名为 LegacySubjectId/Kind/Name，
仅为尚未迁移数据保留原 JSON 名称。DeviceId 是设备维度投影，不是独立的第二个 Target。
Dashboard 使用 groupTargets、targetFilter 和直接 Target 归组、筛选、标注；兼容旧 Browser 时通过
已知 DeviceId 关联到同一设备，不给 Browser 伪造持久 Target。不同 Observer 的原始事实不会合并。
OpenAPI 客户端已重新生成。已迁移行的设备过滤直接使用 Target；Report 保持仅统计明确的 System 活动。
Browser/VRChat 尚未迁移的 null Target 行暂经原 Subject 路径查询，未知内容仍可经家族 API 读取。

任务 05 必须逐项关闭以下消费者与适配，不能仅删除 nullable 或改名：

| 兼容位置 | 当前消费者 | 移除门槛与验证 |
| --- | --- | --- |
| Runtime 旧 System 信封补齐 | 升级前 System outbox、原 v1–v3 Runtime JSON | 已部署版本全部迁移且旧缓存已排空/验证升级；保留升级 fixture，确认不重新分配身份 |
| FactStore 无字段输入补齐 | 改造前第一方 `/facts` 上传、未迁移 Browser/VRChat | 三个 Collector 新写入已显式携带有效归属，旧客户端退出，缓存处理证据齐备 |
| LegacyImport | 更早 ActivitySegment/InputEvent 缓存 | 旧缓存排空或已有可执行升级路径；旧新入口收敛到同一家族事实，无第二份 Payload |
| null Target 查询回退 | 未映射 Browser/账号/其他历史 | 各批有依据数据完成回填，未知历史也能直接查询，再移除 Stream→Subject 业务归属回退 |
| 查询 DTO 的 legacy subject JSON 别名、Dashboard 的旧设备回退 | 尚未迁移 Browser/VRChat 与历史查询结果 | 对应数据和消费者迁移后随任务 05 移除；验证新 System 响应无 subject 字段，跨来源设备展示和公开查询仍成立 |
| 数据 smoke 的 Subject 回退 | 已部署家族基线和逐批升级数据库 | 目标切换完成，脚本也直接检查 Target；不因 schema 变化让验收入口失效 |

System 真实窗口/输入刺激、系统权限及 Windows 现场验收与自动 HTTP、迁移、协议测试分别记录在任务 01；
宿主启动成功只证明组合和起停，不能宣称已覆盖真实观测。

本次按用户确认，System 范围内语义已变化的字段和消费变量使用新名。CollectorInstanceId、StreamId
仍分别表示 Runtime 实例与交付流，不因新增 Observer/Target 改名；旧初始化协议的 Subject 仍服务
未迁移实例元数据，由适配边界读出设备引用，退出条件同任务 05。

2026-09-10（02 接续）：[Browser 实施记录](browser-observation-targets.md) 扩充 application-context
引用、应用上下文表与查询；Runtime 当前写格式升至 v5。本文上方 v4/device-only 描述为 01 交付
时的批次边界。01 的真实 System 门禁仍未关闭。
