# ADR-0019：单 Owner Hub 的状态与 Collector 登录

## 状态：已接受

## 日期：2026-09-20；2026-09-21 收简；2026-09-27 登录流程收简

## 背景

服务器采集需要在 Desktop 离线时继续运行。Web 需要展示各处 Hub 的连接、采集与交付状态，并帮助用户完成 VRChat 登录。2026-09-27 业务复核明确：当前不需要通用远程配置或启停；此前的 configure/start/pause/remove 流程增加了没有业务必要的步骤。

Hub 在线只表示近期联系了 API，不代表 Collector 正常采集或 Record 成功交付。第三方账号认证失效可以单独中断采集。

## 决策

- 每个 Hub 只属于一个 Owner，可以承接多个 Collector；API 服务多个 Owner，Web 按 Owner 读取状态和发起登录。
- Record 沿 Collector → Hub → API 交付，Web 从 API 读取展示。Desktop 是有原生 UI 的宿主，组合自己的 Hub 和平台 Collector；服务器 Hub 不依赖 Desktop。
- Web 的 Collector 操作只保留登录：选择在线 Hub 和已安装类型，提交认证输入；由认证结果确定 Target 并建立 Collector，不要求用户预填账号 ID。首次登录成功自动开始，认证失效后重新登录成功自动恢复。验证码未完成或认证失败不启动采集。重新登录已有 Collector 必须匹配原账号。
- 登录交互沿 Web → API → Hub → Collector 转交，具体 Collector 实现第三方认证与会话恢复。Hub/API 不认识 VRChat 字段或 Record 协议。服务器入口注册编译时安装的 Collector；不建设动态插件安装、升级或通用生命周期框架。
- 账号清单和成功认证后的加密会话以执行 Hub 本地为权威。清单只存 Key/Target，重启自动恢复会话和采集；密码、验证码不持久化。未完成登录只留在 Hub 内存，一个 Hub 同时一个，五分钟失效；后续输入必须匹配 Hub 发出的登录会话标识。新登录替换旧的未完成会话。
- API 只持久化 Hub 登记和离线展示需要的最近状态：Collector 标识/名称/状态/错误、交付积压/失败。登录类型表单仅来自在线报告，不写数据库。API 不保存凭据、第三方会话或持久命令队列。
- API 在线转交登录，执行后应答。离线不接收请求，重启不补放，超时表示结果未知，需刷新实际状态。当前中继面向单 API 实例。
- Desktop 通过 Web 展示状态；原生 UI 负责本地启停。暂停采集不暂停 Hub 交付，Desktop 重新打开自动开始。
- Hub 身份随独占持久目录保存，进程锁防止同目录重复启动；API 拒绝在线身份的另一会话覆盖报告。旧 Hub 可以离线并退役；退役只阻止管理重连，不停止远端进程或撤销 Record 上传凭据。
- 同一账号在一处采集由使用者保证；不引入跨 Hub 的绑定、自动迁移、排他执行或网络分区选主。迁移前先停止旧采集，再在新 Hub 登录。

## 后果

用户完成登录即可开始使用，不再需要先建空 Collector、保存配置再点击开始。状态展示仍区分联络、采集和交付。服务器本地文件只记录成功识别的账号；API 状态快照不能恢复或覆盖这些账号和会话。

每个 Hub 使用 `HubLocalStorage` 统一管理独立目录内的身份、文档、SQLite 队列和加密会话。服务器使用 `Hub:DataDirectory`，Desktop 使用客户端目录，API key 继续保存在系统凭据库。服务器 AES-GCM 的密钥与密文由运行账号持有，不隔离同一 OS 账号；传输使用 HTTPS。

本次替代原先的通用配置、远程启停与移除协议，不保留重写期兼容分支。具体接口及请求/会话期限见 [Hub 契约](../hub-management.md)，运行入口见 [服务器 README](../../src/Server/README.md)。

## 参考

- [领域语言](../../CONTEXT.md) — Hub、Owner、Collector 与在线状态。
- [ADR-0009](ADR-0009-delivery-belongs-to-hub.md) — Collector、Hub 与后端的交付责任。
- [ADR-0014](ADR-0014-hub-desktop-and-server-hosting.md) — Desktop 与服务器部署。
- [ADR-0016](ADR-0016-desktop-host-and-credentials.md) — 宿主组合及避免写死采集协议、严格生命周期框架的约束。
