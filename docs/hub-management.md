# Hub 在线管理

决策权威为 [ADR-0019](adr/ADR-0019-single-owner-hub-management.md)。共享 .NET 消息定义在 `src/Contracts/Heartbeat.Contracts/HubManagement.cs`，管理运行逻辑在 `Heartbeat.Hub.Runtime`。Record 提交与交付继续遵守 [Hub 交付契约](hub-record-delivery.md)，两条链路相互独立。

## 身份与状态

Hub 用持久数据目录中的 `hub-id` 标识自己，进程每次启动创建新的会话 ID。API 从认证令牌取得 Owner；不接受请求体指定其他 Owner。同一个 Hub 身份的另一个会话在原会话仍在线时不能覆盖报告。重启后最多等待在线窗口过期即可重新接入。

Hub 每 5 秒联络 API；最近 30 秒内的有效联络表示在线。Web 同样每 5 秒刷新，API 请求失败时显示状态未知并禁用操作。报告独立包含已安装类型、Collector 实际状态、公开配置和 Record 队列积压/失败。没有 Record 不代表离线；报告中的采集状态在失联后只是最后快照。

数据库只使用 `hubs` 保存身份、Owner、最近联络、退役状态和精简的展示快照。快照保留 Collector 标识/名称/状态/错误与交付状态，不保存公开配置或已安装类型表单。在线配置和类型来自当前 Hub 的内存报告；API 重启后需等待 Hub 再次联络，离线时不展示配置副本。Collector 类型描述提供表单字段，通用 API、Web 和 Hub 不解析具名 Record 协议。

## HTTP

所有路径位于 `/api/v1/hubs`，均要求现有 Bearer 认证。只允许访问本 Owner 的 Hub。

| 方法与路径 | 行为 |
| --- | --- |
| `GET /` | 返回 `{ hubs: [...] }`，包含在线/退役、最近联络及报告 |
| `POST /{id}/check-in` | Hub 提交 `{ sessionId, report, result? }`；返回 `{ command }`，无操作时 command 为 null |
| `POST /{id}/operations` | Web 提交 `{ action, key, target, configuration? }`；等待 Hub 返回 `{ id, succeeded, error? }` |
| `POST /{id}/retire` | 退役已离线 Hub；后续管理联络拒绝接入 |

操作为 `configure`、`start`、`pause`、`remove`。同一 Hub 同时接受一个操作；消息只在当前 API 进程内短暂保存，至多下发一次。默认 20 秒未确认返回 504，表示结果未知，不表示操作没有执行；客户端取消也不能撤销已下发操作。刷新后根据实际状态重试，不自动重放。Hub 也检查截止时间。

服务器配置先暂停实例，再应用并保存公开设置；凭据需求由具体 Collector 报告。保存配置不自动开始。服务器启停选择持久保存，重启恢复。移除停止采集并清理该实例的配置与会话，保留已接管 Record。Desktop 只暴露现有平台 Collector 的开始/暂停，两种入口复用同一运行操作；暂停后重新启动 Desktop 会自动开始。

## 边界

API 当前按单实例运行管理中继；多副本负载均衡需要另行设计连接路由。API 重启丢弃未完成操作，Hub 下一次联络恢复管理；不持久化命令，不接受离线配置，不自动迁移或接管。

同一 Collector 只配置一处由使用者保证；API 不保存绑定或强制排他。迁移前由使用者停止旧采集，再在新 Hub 配置和认证。退役不会远程杀进程，也不撤销 Owner 的通用 Record 上传凭据；网络分区下不保证严格单实例执行。复制持久目录也不能用于并行扩容。

第三方密码/验证码只在操作传输和运行内存短暂使用，部署时沿用 HTTPS。API 不记录请求体；在线报告只能含公开配置，数据库进一步排除全部配置字段。服务器本地 AES-GCM 密钥与密文由运行账号持有，不抵御同一 OS 账号读取。API key 仍使用现有 Hub 环境配置；Desktop 使用系统凭据库。

## Hub 本地存储

每个 Hub 通过 `HubLocalStorage` 使用自己指定的数据目录，身份、JSON 文档的原子替换、队列路径和加密会话入口集中在该模块。CollectorManager 按逻辑名称 `collectors` 读写，DesktopProfile 按 `settings` 读写，不自行决定文件布局。

服务器只指定 `Hub__DataDirectory`；Desktop 沿用客户端数据目录。每个目录包含 `hub-id`、`hub.sqlite`、需要的配置文档与 `secrets/`。指定目录用于该 Hub 独占的数据存储；Unix 上收紧为仅运行账号可读写。进程锁避免并发占用，不同 Hub 目录互不共享状态。Desktop API key 仍交给系统凭据库。这里没有远端配置中心，API 的状态快照不能恢复或覆盖本地配置。

## 最近收发数量

收发图以 [ADR-0024](adr/ADR-0024-delivery-activity-is-ephemeral.md) 为准。五秒管理报告不变，计数独立每秒上报与读取。

- `POST /{id}/activity`：`{ sessionId, activity: { epoch, capturedAt, accepted, delivered } }`。Bearer Owner 与当前活跃管理会话必须匹配；请求上限 1 KiB。
- `epoch` 标识本次 Hub 进程运行；`capturedAt` 为 Hub 的 Unix 毫秒。`accepted` 是成功接管的快照累计数，`delivered` 是核对成功回执的快照累计数。同一 Record 后续更新或再次提交也计数，失败上传不计入 delivered。
- 两个计数是非负 JavaScript 安全整数；同 epoch 的采样时间与计数不得倒退。恢复旧队列后 delivered 可以大于本次运行的 accepted，不能据两者差值计算待上传数量。
- `GET /activity` 返回 `{ activities: { "<hub-id>": { epoch, capturedAt, accepted, delivered } } }`，只含当前 Owner 的最新快照；四秒未更新或管理失联即不返回，响应不缓存。
- Hub/API 不保留秒桶、Record 内容或图形历史。API 重启后等待正常管理接入和新快照；计数不写 PostgreSQL、不刷新在线状态。

Web 与原生界面分别计算相邻快照的 accepted/delivered 差值，显示「已接受 / 已上传」。横轴为时间，纵轴为本次更新间隔内的 Record 快照数量；不换算每秒速率，不做滑动平均。当前视图只在内存中保留最近 60 秒的显示点，初次读取只建立基线。相同或倒序快照不重复绘制；断连后以新快照重新建立基线并留空，Hub 重启或页面刷新重新开始。

Web 悬停显示这个更新间隔的起止时间与两项数量。队列积压、失败仍由已有管理报告单独展示。平滑曲线不超出相邻显示点范围，减少动态时停止连续滚动；这些显示行为不改变 Hub 接管、上传或重试语义。
