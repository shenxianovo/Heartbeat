# VRChat Collector API 调研

> 核实日期：2026-09-20。这是来源事实与实现建议，不是已接受的领域决策。没有使用真实账号调用 API，也没有验证账号在游戏内、仅网站在线、游戏离线时的实际响应。

## 结论与来源边界

VRChat 没有官方支持的公开 API 契约。官方 Creator Guidelines 认可社区文档作为信息来源，但明确它非官方、端点可能无通知改变。技术事实以下采用社区维护的 OpenAPI 原始定义，固定到 [`33275fa2d518f265d42fead9d2b154a24a83c734`](https://github.com/vrchatapi/specification/tree/33275fa2d518f265d42fead9d2b154a24a83c734)（2026-09-18）。不能把社区 SDK 称作 VRChat 官方 SDK。[官方说明](https://hello.vrchat.com/creator-guidelines#api-usage-bots)

HTTP 与 cookie 在技术上不依赖 Desktop 或游戏客户端，可以在无头 .NET 进程执行；但官方规则明确反对应用索取或保存他人的密码、令牌和会话，且假设账号操作来自用户设备与 IP。因此“自托管 Owner 访问自己账号”与“平台托管其他人的账号”不能混称为已经获得官方支持的服务器集成。本文没有判定特定部署得到许可。[官方规则](https://hello.vrchat.com/creator-guidelines#api-usage-bots)

**不能使用 `/auth/user` 的 `state` 判断游戏是否在线。** 社区 [`UserState.yaml`](https://github.com/vrchatapi/specification/blob/33275fa2d518f265d42fead9d2b154a24a83c734/openapi/components/schemas/UserState.yaml) 明确注明该端点的 state 总是 offline。但这不否定该响应的 `presence.world` / `presence.instance` 可作为位置读数；main 已采用后者，见下节。`/users/{自己的账号ID}` 是状态采集的备选，尚未验证自身查询是否存在特殊过滤，不应只根据 schema 就替换 main 的位置读取路径。

## main 既有实现与复用边界

只读检查的 main 为 `86911e75459b0038eeba8aec623d2f3d2890a1f3`。以下路径属于该 Git ref，不代表当前工作树仍存在；使用 `git show main:<path>` 查看，无需切换工作树。

| main 路径（相对仓库） | 已有行为 | 复用建议 |
|---|---|---|
| `collection/collectors/Heartbeat.Collector.VRChat/VRChatApi.cs` | `IVRChatApiFactory` / `IVRChatApiSession`；`VRChat.API` SDK；Basic 登录、emailOtp/totp、cookie 恢复与导出、401 与瞬时错误分类 | 这是最独立、最值得移植的适配器；不依赖旧 Hub 或 CollectorProtocol |
| `collection/collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj` | `VRChat.API` 2.20.8；同时引用旧 Core 与 CollectorProtocol | 可复用 SDK 选型，但不带回旧项目引用 |
| `collection/collectors/Heartbeat.Collector.VRChat/VRChatManagedCollector.cs` | Challenge 登录、Secret 保存、1 分钟轮询、授权续期、发布与 drain | 复用登录步骤与测试场景，不能原样搬入新 Hub：依赖 Activation/Challenge/Drain/旧 Fact 与 Gap |
| `collection/collectors/Heartbeat.Collector.VRChat/PresenceStateMachine.cs`、`PresenceFactPublisher.cs` | 对账号/世界/实例连续性生成有 Revision 的 Segment | 观测字段和账号切换场景可保留；旧 Fact、Revision、兼容身份不能套入当前 Record 模型 |
| `collection/collectors/Heartbeat.Collector.VRChat/VRChatPresenceCheckpoint.cs` | Active、Pending、恢复 Gap、历史 schema 升级 | 不移植旧格式迁移与兼容层；当前重写明确不兼容旧数据 |
| `collection/collectors/Heartbeat.Collector.VRChat/Program.cs`、`VRChatPackageBuilder.cs` | stdio 客户端、能力版本协商、Package 制品与安装链路 | 不带回插件平台；本轮由服务器应用组合已安装 Collector |
| `collection/collectors/Heartbeat.Collector.VRChat.Tests/*` | 授权、账号隔离、重启恢复、世界变化、进程与 Package 场景 | 挑选符合当前风险的场景重新对接，不能把旧 mock 测试当作真实 API 证据 |

main 的 `GetPresenceAsync` 调用 `GetCurrentUserAsync`，读取 **`user.Presence.World` 与 `user.Presence.Instance`**，同时从同一响应取 `user.Id`。它没有使用 `user.State`，也没有把登录成功等同于游戏在线。缺少世界/实例，或者世界为 `offline` / `private` 时返回 null。当前社区 [`CurrentUserPresence` schema](https://github.com/vrchatapi/specification/blob/33275fa2d518f265d42fead9d2b154a24a83c734/openapi/components/schemas/CurrentUserPresence.yaml) 确实包含可为空的 `world`、`instance`、`status`、`platform` 等字段，因此应优先复用 main 的字段路径，并把 null 表达为没有可用位置观测。schema 没有承诺缺失 presence 等于游戏下线。

main 的 README 与 `docs/architecture/vrchat-account-observation.md` 明确自动测试使用 mock，真实账号现场验收尚未执行。这意味着复用可以减少代码重写，但不能省去真实登录与 presence 可见性验证。

移植时需要顺手修正的局部风险：未知 2FA method 不能默认当 totp；验证响应必须检查 verified；会话导出只有 cookie name/value，丢失过期等属性；轮询的瞬时失败目前没有增长退避，世界名每次都查也没有缓存；凭据登录的 SDK 实例继续持有 username/password。以上是代码检查得到的事实及建议，不要求带回旧生命周期框架。

## 请求与认证

API base URL 为 `https://api.vrchat.cloud/api/1`。每次请求必须带可识别的 `User-Agent`，格式为 `applicationName/Version contactInfo`；Heartbeat 应要求真实联系信息，不冒充官方客户端。[API 示例](https://vrchat.community/reference/get-current-user)、[官方 User-Agent 要求](https://hello.vrchat.com/creator-guidelines#api-usage-bots)

| 步骤 | 请求 | 处理 |
|---|---|---|
| 初始认证 | `GET /auth/user`；`Authorization: Basic base64(urlencode(username):urlencode(password))` | 用户名与密码先分别 URL 编码，再以冒号连接并 Base64；接收 `Set-Cookie` |
| 已有会话 | `GET /auth/user`；发送已有 cookie | 有效 auth cookie 会复用会话，避免反复密码登录造成会话限流 |
| 需要邮箱 OTP | 响应 `{"requiresTwoFactorAuth":["emailOtp"]}` 后 `POST /auth/twofactorauth/emailotp/verify` | 沿用登录中的 cookie，JSON body 为 `{"code":"123456"}` |
| 需要 TOTP | 响应 `{"requiresTwoFactorAuth":["totp"]}` 后 `POST /auth/twofactorauth/totp/verify` | 同上；不需要 Collector 保存 TOTP seed |
| 完成认证 | 验证响应 `{"verified":true}` 后再次 `GET /auth/user` | 必须得到 CurrentUser 与 `id`，不能把 HTTP 200 本身当作登录完成 |

`totp` 验证响应还可包含 `enabled`。遇到未知的挑战类型，应报告需要人工处理，不推测路径。认证 cookie 名为 `auth`，2FA cookie 名为 `twoFactorAuth`；应使用 CookieContainer 处理域、路径、过期与更新。不存在已核实的通用 OAuth 登录流程。[认证定义](https://github.com/vrchatapi/specification/blob/33275fa2d518f265d42fead9d2b154a24a83c734/openapi/components/paths/authentication.yaml)、[邮箱验证](https://vrchat.community/reference/verify2faemail-code)、[TOTP 验证](https://vrchat.community/reference/verify2fa)、[社区 .NET SDK cookie 示例](https://vrchat.community/dotnet)

## 状态查询备选（尚未选择）

若本轮还需读取 state/status，可在认证完成后以返回的 `id` 作为 Target，尝试 `GET /users/{id}`；不使用 displayName 或登录用户名充当稳定身份，仅访问自己的 ID，不查询好友列表。端点需要 auth cookie。此备选不能替代对 main 的 presence 路径复用。[端点定义](https://vrchat.community/reference/get-user)

以下是从 User schema 选择的字段形状示例，不是一次真实抓取；`location`、`worldId`、`instanceId` 等可能缺失。

```json
{
  "id": "usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469",
  "displayName": "Example",
  "state": "online",
  "status": "active",
  "statusDescription": "",
  "location": "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345",
  "worldId": "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd",
  "instanceId": "12345"
}
```

建议 Collector 只保留这些字段和本地观测时间，作为 Point Record 表达一次 API 读数。不要持久化完整 JSON：响应包含与本轮目标无关的账号或社交信息。不要把两次采样间没有观测的时间推断成连续游戏时间。[User schema](https://github.com/vrchatapi/specification/blob/33275fa2d518f265d42fead9d2b154a24a83c734/openapi/components/schemas/User.yaml)

- `state` 的社区语义：`online` 为在 VRChat 内在线；`active` 为在线但不在 VRChat 内；`offline` 为离线。保留未知值，不悄悄映射成 offline。[UserState](https://github.com/vrchatapi/specification/blob/33275fa2d518f265d42fead9d2b154a24a83c734/openapi/components/schemas/UserState.yaml)
- `status` 包含 `active`、`ask me`、`busy`、`join me`、`offline`，混合状态与隐私偏好，不能替代 `state`。[UserStatus](https://github.com/vrchatapi/specification/blob/33275fa2d518f265d42fead9d2b154a24a83c734/openapi/components/schemas/UserStatus.yaml)
- 位置字段有访问可见性：`location` 可为 `offline`；`instanceId` 可为 `offline` 或 `private`。缺失或占位值不等于确认游戏下线，不据此合成在线布尔值。[LocationID](https://github.com/vrchatapi/specification/blob/33275fa2d518f265d42fead9d2b154a24a83c734/openapi/components/schemas/LocationID.yaml)、[InstanceID](https://github.com/vrchatapi/specification/blob/33275fa2d518f265d42fead9d2b154a24a83c734/openapi/components/schemas/InstanceID.yaml)

## 运行建议

没有找到官方公布的每分钟请求配额或保证安全的轮询周期。**60 秒一次并加入随机抖动是本项目建议，不是 VRChat 的额度承诺。** 请求结束后再延迟，避免慢请求重叠；不要按每个整分钟同步触发。429 立即退避，存在 Retry-After 时尊重该值，否则指数退避并限制上限；网络或 5xx 同样退避。401/403 与 2FA 要求进入需要处理的状态，不能反复自动密码登录。[官方请求节制要求](https://hello.vrchat.com/creator-guidelines#api-usage-bots)、[社区限流说明](https://vrchat.community/faq)

凭据处理是实现建议：密码和一次性验证码仅用于本次登录，不写入 Record、日志、命令持久化或状态快照；会话只留在执行 Collector 的 Hub 本地受保护存储，远程状态仅返回是否已认证。不要回传 cookie 给 Web，不记录 Cookie、Set-Cookie、Authorization 或原始异常响应体。登录后复用会话，cookie 过期时要求重新认证。默认使用固定 HTTPS origin，禁止携带凭据自动跳转到其他域名。若提供测试 origin，应只在测试依赖中注入，不成为任意用户可配置的凭据发送目标。

真实验收至少覆盖：仅 API 已认证但游戏关闭、游戏在线、退出游戏、网站在线但游戏关闭、认证过期、邮箱 OTP/TOTP、目标 ID 一致性和 429。验收前不能宣称已验证服务器能可靠还原游戏在线状态或所在实例；Mock 测试只证明客户端按已知响应处理正确。
