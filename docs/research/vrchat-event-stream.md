# VRChat 事件与同实例观测调研

> 核实日期：2026-09-27。本文记录来源事实与实现建议，不是已接受的架构决策。没有访问真实凭据、连接真实账号或读取用户游戏日志；SDK 和日志格式尚需目标设备现场验收。

## 结论

需求已经明确为“与我同一实例的人进出，统计相遇和共处时间”。**单独接 VRChat Pipeline WebSocket 不能完整满足它；推荐以游戏设备上的本地日志事件为观测来源。** Pipeline 提供自己位置和好友状态变化，并非当前实例所有玩家的进出订阅；好友位置还受隐私状态限制。VRCX 的实现也分别处理 Pipeline 与本地游戏日志。[Pipeline 原始文档][pipeline]、[VRCX WebSocket][vrcx-ws]、[VRCX 日志解析][log]

建议的职责划分（待确认）：本地 Collector 读取并规范化世界/实例、玩家进入/离开与游戏会话结束事件；Hub 接管提交；展示层从这些观测计算访问次数、停留时长和同实例时间。API 可补充缓存中的展示资料；不能让好友在线位置代替本地同实例事件。这涉及组件责任及数据权威，实施前需按仓库规则确认并留 ADR。

2026-09-27 后续范围确认：用户选择先实现服务器 Hub 能提供的自己位置与好友可见同场；本地日志暂缓，其跨来源去重与互补单独跟踪。因此本文“日志更完整”的来源结论不等于已批准接入日志。当前实施决定见 [ADR-0026](../adr/ADR-0026-vrchat-server-event-observation.md)。

## Pipeline 能提供什么

技术来源固定到社区文档 [`106733066aaf870815ea7c4665c78a404f2195fd`](https://github.com/vrchatapi/vrchatapi.github.io/tree/106733066aaf870815ea7c4665c78a404f2195fd)。这是社区逆向记录，VRChat 没有承诺公开、稳定的 API 契约。[官方边界][guidelines]

| 事件 | 观测含义 | 不能据此声称 |
| --- | --- | --- |
| `user-location` | 当前认证账号的实例变化；读取顶层 `location`、`travelingToLocation` | 所有同场者的名单和进出 |
| `friend-location` | 好友位置变化；可返回 `private` 或 `traveling` | 位置隐藏的好友已离开，或传送目标已经抵达 |
| `friend-online` | 好友在游戏内上线 | 已进入我的实例 |
| `friend-active` | 好友在网站活跃 | 正在游戏内与我共处 |
| `friend-offline` | 好友离线 | 精确的客户端退出时刻 |
| `friend-add` / `friend-delete` | 好友关系改变 | 同场相遇开始或结束 |

以上事件及字段来自[原始事件定义][pipeline]。比较完整的实例位置最多产生“API 可见位置相同”的判断；把它解释为完整同场观测是额外推断，尤其无法覆盖非好友和隐藏位置。

连接使用 `wss://pipeline.vrchat.cloud/?authToken=...` 与合规 User-Agent；token 来自登录会话。消息的 `content` 通常是需要再次解码的 JSON 字符串。错误消息可能包含 token，因此不能直接记录完整消息或连接 URL。[连接文档][pipeline]

没有在该文档与下面的客户端实现中找到位置事件的可靠服务器发生时间、事件序号、重放游标或断线补发保证。这是“未找到保证”，不是断言服务端永远不补发。实现应保存本地接收时间并标明其语义；重连后的快照只能重建当前读数，不能还原断线期间的历史。[Pipeline][pipeline]、[SDK 消息包装][sdk-message]、[VRCX 重连][vrcx-ws]

初次连接仍需 REST 认证；若要立即知道已在线好友而不是等待其下一次变化，还需好友快照。VRCX 在好友数据加载后才连接，异常重连后刷新好友及通知；这证明它没有仅靠连接自动恢复完整状态。[VRCX 连接与重连流程][vrcx-ws]。快照和事件之间的竞态仍需自行处理，不能假设原子快照。

## .NET SDK 与限流

社区 .NET SDK 上游已有独立的 `VRChat.API.Realtime` 包及 `OnUserLocation`、`OnFriendLocation`、`OnFriendOnline`、`OnFriendOffline` 等回调。本次源码固定为 [`bd2b89ee8c3e4c95e9d2259b9fdb84c2bbda3d7f`](https://github.com/vrchatapi/vrchatapi-csharp/tree/bd2b89ee8c3e4c95e9d2259b9fdb84c2bbda3d7f)，不代表 Heartbeat 当前引用的 REST 包已带上它，也未核实选定版本的 NuGet 集成。[安装说明][sdk-doc]、[事件派发实现][sdk-process]

不要直接把 SDK 自动重连当作完整恢复策略：当前代码断开后等待 2 秒尝试重连；没有在该路径发现指数退避或历史重放。它还发送应用层 heartbeat，而社区文档将连接描述为仅接收；两者存在文档差异，正式采用前应通过实际协议验证，不盲目复制。[SDK 客户端][sdk-client]、[Pipeline][pipeline]

事件推送能减少位置轮询，但不保证没有 429。认证、快照、世界资料仍是 HTTP 请求；重连风暴也不应放任。官方要求缓存、限速、遇 429 退避并避免整点同步轮询，没有承诺本应用可用的固定请求配额。建议采用可取消的指数退避与抖动，HTTP 返回 `Retry-After` 时尊重它，不通过反复登录解决限流。[官方 API 要求][guidelines]

**本地日志进出采集本身不调用 VRChat API，因此不会因为每个玩家进出而产生 429。** 这也是当前同实例需求选择日志路径的直接收益；如果补查头像、世界资料，其请求仍需共享缓存与限流。这是根据数据路径作出的实现判断。

## 本地日志提供的证据

VRCX 源码固定为 [`746d2de9c70d92b2b0c3c679a8b9de483927726e`](https://github.com/vrcx-team/VRCX/tree/746d2de9c70d92b2b0c3c679a8b9de483927726e)。以下是它所解析的格式，不是官方稳定日志 schema：

| 本地日志信号 | VRCX 行为 | Heartbeat 应保留的边界 |
| --- | --- | --- |
| `[Behaviour] Entering Room: ...` | 保存世界名称 | 展示资料，不当作玩家已全部加载 |
| `[Behaviour] Joining wrld_...:...` | 记录完整实例位置 | 与传送目标、成功抵达分开核实；不能把名称当身份 |
| `[Behaviour] OnPlayerJoined Name (usr_...)` | 记录玩家进入，解析显示名和 ID | 进入是本客户端观察到玩家，不证明其更早的活动 |
| `[Behaviour] OnPlayerLeft Name (usr_...)` | 记录玩家离开 | 缺失事件不等于玩家继续在场 |
| `[Behaviour] OnLeftRoom` | 产生位置离开/目的地事件 | 我离开时结束当前同场会话 |
| `VRCApplication: OnApplicationQuit ...` / `HandleApplicationQuit ...` | 记录正常退出 | 崩溃可能没有这一行，需独立结束不确定区间 |

依据：[位置/离开解析][log-location]、[玩家解析][log-player]、[正常退出解析][log-quit]。VRCX 玩家解析不按好友过滤，因此可以覆盖日志实际记录到的非好友；这仍不是对所有客户端情形都不丢事件的保证。

玩家行有 `usr_...` 时可以按稳定账号 ID 关联。VRCX 同时接受只有显示名、没有 ID 的行；Heartbeat 不能把显示名强行升级为稳定身份或把重名记录自动合并，应保留未解析身份，并通过当前客户端样本确认字段可用性。[ID 提取实现][log-id]

VRCX 读取每行前 19 个字符 `yyyy.MM.dd HH:mm:ss`，按设备本地时区转 UTC；精度为秒，并且代码明确处理夏令时异常。因此日志时间不是可信服务器时钟；采集设备时区、时钟变化和同秒事件顺序必须纳入设计。建议另存文件内顺序/偏移以保证确定性去重与重放，而非仅用时间戳去重。[时间和增量读取实现][log]

## 运行条件和缺失处理

官方确认 PC 默认写入文本输出日志，Windows 目录为 `%AppData%\..\LocalLow\VRChat\VRChat`；每次启动一个 `output_log_*.txt`。日志保留 24 小时，启动时会删除更旧日志。因此要持续收集，或及时导入仍存在的日志，不能承诺任意历史补录。[官方日志说明][official-logs]

用户设备为 Windows/PCVR 与 Quest 独立运行两种。Quest 新安装默认只记错误，需在 Quick Menu 的 Settings → Debug 选择 Full；连接 PC 后可在设备根目录 `Documents/Logs` 取得日志。Android/iOS 也默认不记录完整日志。仅在服务器或另一台 Mac 上持有 API 会话，并不能读到游戏设备的文件；建议 Windows 实时采集，Quest 先提供显式日志导入；官方文档证明的是文件取得方式，并没有提供自动向服务器推送事件的接口。该分工仍待架构确认。[Quest/PC 说明][official-logs]、[移动端说明][mobile-logs]

实现建议：只提取必要事件，不上传整个诊断日志；持久化读取位置，处理日志轮换、截断、半行写入、重复读取和不同会话；缺少进入边界、日志不可读、设备休眠或游戏崩溃时显示 Observation Gap。已经落盘但尚未提交的事件可恢复读取；从未写出或已删除的事件无法通过 API 补回。

“相遇次数”建议按同一实例中双方可观测重叠的一段会话计算，“共处时间”按这些重叠区间求和，不能解释成实际交谈或注意力。连续性与中断收口策略仍是待确认的协议语义，本文不将其视作已批准设计。

验收需覆盖：我进入已有人的实例、陌生人进出、好友隐藏位置、传送未成功、我先离开、游戏正常退出/崩溃、日志中途启用、Collector 重启及重复读取、日志截断/删除、时区与同秒事件。按照用户要求，事件真实性只通过人工端到端验收：分别在 Windows/PCVR 和 Quest 上执行受控进出，按实际操作对照当前客户端日志、规范化事件及最终统计。本文不建议以模拟单元测试证明真实字段或时序可靠。

[pipeline]: https://github.com/vrchatapi/vrchatapi.github.io/blob/106733066aaf870815ea7c4665c78a404f2195fd/content/tutorials/websocket.markdown
[guidelines]: https://hello.vrchat.com/creator-guidelines#api-usage-bots
[vrcx-ws]: https://github.com/vrcx-team/VRCX/blob/746d2de9c70d92b2b0c3c679a8b9de483927726e/src/services/websocket.js
[log]: https://github.com/vrcx-team/VRCX/blob/746d2de9c70d92b2b0c3c679a8b9de483927726e/Dotnet/LogWatcher.cs
[log-location]: https://github.com/vrcx-team/VRCX/blob/746d2de9c70d92b2b0c3c679a8b9de483927726e/Dotnet/LogWatcher.cs#L342-L461
[log-player]: https://github.com/vrcx-team/VRCX/blob/746d2de9c70d92b2b0c3c679a8b9de483927726e/Dotnet/LogWatcher.cs#L463-L544
[log-quit]: https://github.com/vrcx-team/VRCX/blob/746d2de9c70d92b2b0c3c679a8b9de483927726e/Dotnet/LogWatcher.cs#L1118-L1137
[log-id]: https://github.com/vrcx-team/VRCX/blob/746d2de9c70d92b2b0c3c679a8b9de483927726e/Dotnet/LogWatcher.cs#L1409-L1429
[sdk-doc]: https://github.com/vrchatapi/vrchatapi-csharp/blob/bd2b89ee8c3e4c95e9d2259b9fdb84c2bbda3d7f/WEBSOCKET.md
[sdk-client]: https://github.com/vrchatapi/vrchatapi-csharp/blob/bd2b89ee8c3e4c95e9d2259b9fdb84c2bbda3d7f/wrapper/VRChat.API.Realtime/VRChatRealtimeClient.cs
[sdk-process]: https://github.com/vrchatapi/vrchatapi-csharp/blob/bd2b89ee8c3e4c95e9d2259b9fdb84c2bbda3d7f/wrapper/VRChat.API.Realtime/VRChatRealtimeClient.MessageProcessor.cs
[sdk-message]: https://github.com/vrchatapi/vrchatapi-csharp/blob/bd2b89ee8c3e4c95e9d2259b9fdb84c2bbda3d7f/wrapper/VRChat.API.Realtime/Messages/WebSocketMessageWrapper.cs
[official-logs]: https://help.vrchat.com/hc/en-us/articles/9521522810899-Where-do-I-find-my-Output-Logs-and-Crash-Dumps
[mobile-logs]: https://help.vrchat.com/hc/en-us/articles/19651108531859-VRChat-Mobile-How-do-I-access-my-Output-Logs
