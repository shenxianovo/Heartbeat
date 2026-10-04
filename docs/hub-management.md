# Hub 在线管理

决策权威为 [ADR-0019](adr/ADR-0019-single-owner-hub-management.md)。共享 .NET 消息定义在 `src/Contracts/Heartbeat.Contracts/HubManagement.cs`，管理运行逻辑在 `Heartbeat.Hub.Runtime`。Record 提交与交付继续遵守 [Hub 交付契约](hub-record-delivery.md)，两条链路相互独立。

## 身份与状态

Hub 用持久数据目录中的 UUIDv7 `hub-id` 标识自己，进程每次启动创建新的会话 ID。API 从认证令牌取得 Owner；不接受请求体指定其他 Owner。同一个 Hub 身份的另一个会话在原会话仍在线时不能覆盖报告。重启后最多等待在线窗口过期即可重新接入。

Hub 每 5 秒联络 API；最近 30 秒内的有效联络表示在线。Web 同样每 5 秒刷新，API 请求失败时显示状态未知并禁用操作。报告独立包含支持登录的类型、Collector 实际状态和 Record 队列积压/失败。没有 Record 不代表离线；报告中的采集状态在失联后只是最后快照。

数据库用统一 `objects` 保存 Hub 身份与 Owner；`hubs.id` 共用该对象主键，保存最近联络、退役状态和精简的展示快照。首次有效联络原子登记身份，跨 Owner 不能认领同一 ID。快照保留 Collector 标识/名称/状态/错误与交付状态，不保存登录表单。支持登录的类型及字段来自当前 Hub 的内存报告；API 重启后需等待 Hub 再次联络。具体 Collector 决定认证字段和账号身份，通用 API、Web 和 Hub 不解析具名认证协议或 Record 内容。

## HTTP

所有路径位于 `/api/v1/hubs`，均要求现有 Bearer 认证。只允许访问本 Owner 的 Hub。

| 方法与路径 | 行为 |
| --- | --- |
| `GET /` | 返回 `{ hubs: [...] }`，包含在线/退役、最近联络及报告 |
| `POST /{id}/check-in` | Hub 提交 `{ sessionId, report, result? }`；返回 `{ command }`，无操作时 command 为 null |
| `POST /{id}/login` | Web 提交 `{ key, input, target?, sessionId? }`；等待 Hub 返回 `{ id, succeeded, error?, login? }` |
| `POST /{id}/retire` | 退役已离线 Hub；后续管理联络拒绝接入 |

Web 只提供 Collector 登录，不提供通用配置、开始、暂停或移除。首次登录选择 Hub 和已安装类型，提交用户名、密码；需要验证码时提交后续输入。`login` 返回 `{ target, fields, error, sessionId }`：`target` 为空表示尚未完成，Web 按 `fields` 显示下一步；成功时由第三方认证结果确定 Target，Hub 保存账号并自动开始采集。用户无需填写账号 ID。同类型尚无账号时显示“登录”；已有账号时入口标为“添加其他账号”，与该账号认证失效时的“重新登录”区分。

已有 Collector 认证失效时，Web 从该行发起重新登录，附带原 `target`；Collector 校验认证账号不能改变归属。成功后自动恢复采集。错误凭据或未完成验证码不会建立新 Collector 或开始采集，也不会替换现有实例。Desktop 仅上报状态，本地原生 UI 继续负责自身启停。

每个 Hub 同时保留一个未完成登录，Hub 生成 `sessionId` 绑定类型和原 Target。后续验证码必须带回它；新登录替换旧会话，五分钟后未完成会话清理。用户名、密码与验证码只在传输和临时会话内使用，Web 每次提交后清空输入。Hub 重启后从用户名、密码重新开始未完成登录。

Web 管理页在宽屏按 Hub 横向排列身份、Record 交付、最近收发和 Collector，窄屏换行展示。Hub 与账号标识默认收起，可展开读取；离线、退役或刷新失败时，Collector 状态明确标为最近上报信息。登录表单在所属 Hub 行下方展开。页面布局不改变看板娘的尺寸与位置。

每个 Hub 同时接受一个中继请求，消息只在当前 API 进程内短暂保存，至多下发一次。默认 20 秒未确认返回 504，表示结果未知；客户端取消不能撤销已下发请求。刷新后根据实际状态重试，不自动重放。Hub 也检查截止时间。这里的请求截止时间与等待用户输入验证码的会话期限不同。

## 边界

API 当前按单实例运行管理中继；多副本负载均衡需要另行设计连接路由。API 重启丢弃未完成操作，Hub 下一次联络恢复管理；不持久化命令，不接受离线配置，不自动迁移或接管。

同一 Collector 只在一处运行由使用者保证；API 不保存绑定或强制排他。迁移前由使用者停止旧采集，再在新 Hub 登录。退役不会远程杀进程，也不撤销 Owner 的通用 Record 上传凭据；网络分区下不保证严格单实例执行。复制持久目录也不能用于并行扩容。

第三方密码/验证码只在操作传输和运行内存短暂使用，部署时沿用 HTTPS。API 不记录请求体；在线报告、数据库状态快照与 Record 都不包含认证输入或会话。服务器本地 AES-GCM 密钥与密文由运行账号持有，不抵御同一 OS 账号读取。API key 仍使用现有 Hub 环境配置；Desktop 使用系统凭据库。

## Hub 本地存储

每个 Hub 通过 `HubLocalStorage` 使用自己指定的数据目录，身份、JSON 文档的原子替换、队列路径和加密会话入口集中在该模块。CollectorManager 按逻辑名称 `collectors` 保存已登录账号的 Key/Target，重启后恢复加密会话并自动采集；无法认证时等待重新登录。DesktopProfile 按 `settings` 读写，不自行决定文件布局。

服务器只指定 `Hub__DataDirectory`；Desktop 沿用客户端数据目录。每个目录包含 `hub-id`、`hub.sqlite`、需要的账号或客户端设置文档与 `secrets/`。指定目录用于该 Hub 独占的数据存储；Unix 上收紧为仅运行账号可读写。进程锁避免并发占用，不同 Hub 目录互不共享状态。Desktop API key 仍交给系统凭据库。这里没有远端配置中心，API 的状态快照不能恢复或覆盖本地配置。

## 最近收发数量

收发图以 [ADR-0024](adr/ADR-0024-delivery-activity-is-ephemeral.md) 为准。五秒管理报告不变，计数独立每秒上报与读取。

- `POST /{id}/activity`：`{ sessionId, activity: { epoch, capturedAt, accepted, delivered } }`。Bearer Owner 与当前活跃管理会话必须匹配；请求上限 1 KiB。
- `epoch` 标识本次 Hub 进程运行；`capturedAt` 为 Hub 的 Unix 毫秒。`accepted` 是成功接管的快照累计数，`delivered` 是核对成功回执的快照累计数。同一 Record 后续更新或再次提交也计数，失败上传不计入 delivered。
- 两个计数是非负 JavaScript 安全整数；同 epoch 的采样时间与计数不得倒退。恢复旧队列后 delivered 可以大于本次运行的 accepted，不能据两者差值计算待上传数量。
- `GET /activity` 返回 `{ activities: { "<hub-id>": { epoch, capturedAt, accepted, delivered } } }`，只含当前 Owner 的最新快照；四秒未更新或管理失联即不返回，响应不缓存。
- Hub/API 不保留秒桶、Record 内容或图形历史。API 重启后等待正常管理接入和新快照；计数不写 PostgreSQL、不刷新在线状态。

Web 与原生界面分别计算相邻快照的 accepted/delivered 差值，显示「已接受 / 已上传」。横轴为时间，纵轴为本次更新间隔内的 Record 快照数量；不换算每秒速率，不做滑动平均。当前视图只在内存中保留最近 60 秒的显示点，初次读取只建立基线。相同或倒序快照不重复绘制；断连后以新快照重新建立基线并留空，Hub 重启或页面刷新重新开始。

Web 悬停显示这个更新间隔的起止时间与两项数量。队列积压、失败仍由已有管理报告单独展示。平滑曲线不超出相邻显示点范围，减少动态时停止连续滚动；这些显示行为不改变 Hub 接管、上传或重试语义。
