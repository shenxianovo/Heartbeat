# Shared Kernel — CONTEXT

## Conventions

- **时间存储**：所有时间字段在数据库中以 UTC+0 存储。"今天"/"本周"的边界由前端根据用户浏览器时区确定，通过 DateTimeOffset 参数传给服务端。
- **认证架构**：依赖外部自建 Auth 平台（支持邮箱/Google/GitHub 登录）。Collection（Agent）使用 Auth 平台签发的 ApiKey 上传数据；Dashboard（前端）计划通过 Auth 平台登录获取 JWT 访问报表 API（尚未实现）。
- **数据隔离**：多用户模式下，User 拥有多个 Device，AppUsage 通过 Device 间接关联到 User，用户只能看到自己 Device 的数据。所有业务端点需 JWT 认证，Service 层显式按 OwnerId 过滤。Agent 通过 `X-Hardware-Id` header 标识设备，服务端用 (OwnerId, HardwareId) 定位设备，首次见到新组合时自动创建。

## Glossary

| Term | Definition |
|------|-----------|
| Device | 一台唯一的物理机器。由 (OwnerId, HardwareId) 联合唯一标识。HardwareId 取自 Windows MachineGuid（HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid）。DeviceName 为纯显示字段（默认取 hostname，用户可改）。属于某个 User（OwnerId = JWT sub claim，string 类型）。 |
| App | 一个应用程序，由进程可执行文件名（不含路径）唯一标识。同一 exe 无论开几个窗口都算同一个 App。 |
| AppUsage | 一段某个 App 处于前台的时间记录（StartTime → EndTime）。系统忠实记录所有前台窗口，包括 explorer.exe（桌面）和 LockApp.exe（锁屏），不做活跃/非活跃过滤。 |
| AppIcon | App 对应的图标二进制数据，由 Agent 上传，供 Dashboard 展示。 |
| ApiKey | Collection 上传数据到 Analytics 的凭证，类似 LLM API Key。 |
| UsageMerger | 合并因上传分片截断而产生的同一 App 碎片记录。规则：同 AppName + 时间间隔 ≤1s 则合并。客户端和服务端双层执行。不做跨切换的聚合——用户切走再切回产生的两段记录是独立的。 |
| InputEvent | 一次键盘按下或鼠标操作的离散事件记录（一行一事件）。由 Agent 通过全局低级钩子（WH_KEYBOARD_LL/WH_MOUSE_LL）采集。EventType 区分 KeyDown(1)/MouseButton(2)/MouseScroll(3)；Code 在键盘事件中为 Windows 虚拟键码原始值，鼠标按钮为 1左/2右/3中，滚轮为 1上/2下。只记按下，KeyUp 仅用于过滤长按自动重复，不落盘。隐私上等价于键盘记录器输出，仅用于单用户自部署的个人统计。主键 Id 为 Agent 生成的 UUIDv7，兼作去重键，保证离线重传幂等（服务端 ON CONFLICT DO NOTHING）。 |
