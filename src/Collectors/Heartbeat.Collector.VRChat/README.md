# VRChat Collector

服务器应用组合 `heartbeat.collector.vrchat`；Target 是登录接口返回的规范 `usr_<小写 UUID>`，每个绑定仅观察自己的账号与好友可见同场，不读取设备游戏日志。责任和语义见 [ADR-0026](../../../docs/adr/ADR-0026-vrchat-server-event-observation.md)。

## 登录和事件

用户名、密码及 email OTP/TOTP 仅用于交互登录，成功后以 Cookie 会话重建客户端并加密保存会话，不保存密码。重启恢复会话并开始采集。认证过期显示需要认证，不自动重复密码登录。

连接 `wss://pipeline.vrchat.cloud`，接收 `user-location`、`friend-location`、`friend-online`、`friend-offline`、`friend-active`、`friend-delete`。只解析必要的账号/显示名/完整位置字段；不持久化原始消息、token、通知正文或好友在其他实例的位置历史。WebSocket 使用协议层 Ping/Pong 检测连接故障，不发送自定义应用层 heartbeat。

连接建立后读取自己的 `CurrentUser.Presence` 和分页在线好友快照，此后每五分钟核对一次。事件接收并行进行：快照请求期间已接收某账号的事件时，该账号的快照被丢弃，避免覆盖较新的事件。快照发现位置变化时不倒推真实离开时刻；旧段停留在上次确认，新段从读到的位置开始。首次看到已在场者同样不回填此前历史。

同一服务器 factory 的 REST 请求共用串行节流，每次完成后至少间隔一秒；429 按 Retry-After 暂停，没有该字段则等待两分钟。世界名缓存最多 256 项，资料请求服从同一节流。WebSocket 重连从 30 秒开始指数退避，基准最多 10 分钟并加 0–20% 抖动，Retry-After 更长时优先；持续运行超过五分钟后重置退避。首次连接/核对失败、事件解析失败、Hub 接管失败都会断开当前连续性并重试；401/403 认证失效需重新登录。状态只展示脱敏错误类别/HTTP 状态和重连等待时间。

## Record 协议

两个 Track 都为 version `1`、`range`、`explicit`：

| Track | value 字段 | 含义 |
| --- | --- | --- |
| `vrchat.location` | `account_id`, `world_id`, `world_name`（可 null）, `instance_id`, `basis: "api_visible"` | 自己的可见世界/实例区间 |
| `vrchat.encounter` | `account_id`, `friend_id`, `friend_name`（可 null）, `world_id`, `instance_id`, `basis: "api_visible"` | API 所见双方完整位置相同的区间 |

同一世界的不同实例不合并为同场。private、traveling、offline、active 或缺少位置时停止同场依据，不从传送目的地推测已到达。事件按服务器接收时间建立边界；REST 按读取响应时间确认。会话内以单调时钟推进绝对时间基准，避免系统校时倒退造成倒序。它们不是 VRChat 保证的真实发生时刻，也不是对实际交谈的判断。

同账号/世界/实例延续同一 Record，仅增长 endedAt；显示名和世界名在建立时冻结，不修改已有 value。自己的位置和好友读数最多相隔六分钟才能形成同场依据。核对断开/超过窗口后新建，最后观测之后不外推，首次读数为零长度 Range。进程崩溃可能丢失 Hub 接管前的内存数据；已接管数据由 Hub 持久交付。交接失败保留待交付的同 ID 快照，重新连接前先重试交接，暂停最后尝试五秒。

待交接快照使用 [Hub Client](../../Hub/Heartbeat.Hub.Client/README.md) 的共享缓冲，按 Collector 与 Track 声明、条数及字节上限分批；成功接管后只释放对应发送快照。连续性和重连策略仍由本 Collector 决定。

## 展示与验收

Web `/vrchat` 按一个 Collector 读取两个 Track，显示世界气泡、实例类型组成、访问明细、活动热力图与可见同场。世界停留按日期范围裁剪；段数是观测段数，重连可能拆段，不能称为精确的访问次数。未知名字退回稳定 ID。UI 明示缺失及 API 可见性限制，不把多个 Collector 来源的时长相加。

[人工 E2E](../../../docs/validation/vrchat-server-events.md) 是真实事件正确性的验收入口。本轮没有新增模拟事件单元测试；已有认证和 Hub 交付测试仅随接口更新，不能证明 VRChat 线上字段或实时性。协议来源与不确定性见[调研](../../../docs/research/vrchat-event-stream.md)。

本地 PC/Quest 日志尚未接入。跨来源去重、互补、权威选择和冲突处理仍待设计，不承诺自动补齐服务器观测。
