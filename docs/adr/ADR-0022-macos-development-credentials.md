# ADR-0022：macOS 开发凭据由独立 profile 的受限文件保存

## 状态：已接受

## 日期：2026-09-22

用户确认优先消除开发重建后的钥匙串密码交互，并接受开发烟测不覆盖正式系统凭据库。实机调查确认：固定自签名证书虽保持 designated requirement 不变，file-based Keychain 的 partition 仍按构建 CDHash 授权；“始终允许”不能覆盖内容改变后的新构建。证据与备选方案见[调查](../research/macos-development-credentials.md)。

macOS 宿主运行时按 Bundle ID 选择凭据存储：`com.shenxianovo.heartbeat.desktop.dev` 使用开发凭据文件，其他身份使用 Keychain。`HeartbeatDevelopmentBuild=true` 只负责让 DevCLI 生成开发名称和 Bundle ID，存储选择独立于 MSBuild 属性以及 Debug/Release 优化配置。开发 profile 默认位于 `LocalApplicationData/Heartbeat/DesktopDev`；其他身份使用 `Heartbeat/Desktop`。开发文件存储实现会编译进普通构建，但不会被普通 Bundle ID 选择。开发 API Key 的持久权威为该 profile 下的 `api-key` 文件，目录权限 `0700`、文件权限 `0600`，写入以同目录临时文件原子替换；不进入 settings、日志或验证证据。不迁移旧凭据，不在 Keychain 错误时自动降级。文件只按系统用户隔离，同一用户的其他进程仍可读取，这是已接受的开发保护边界。

凭据实现的选择属于 macOS 宿主；共享 Desktop 运行模块、Auth、Hub 与 Collector 不感知开发存储。Windows 保留 Credential Manager。ADR-0016 的系统凭据库存储约束继续适用于普通 macOS 构建与 Windows；本决策为显式 macOS 开发身份细化其范围。ADR-0020 的本地固定签名保留，用于稳定 TCC 身份；ADR-0021 的服务器 `.env.local` 与桌面凭据仍各自独立。

`env up desktop` 与 `desktop-replay` 使用相同开发存储。烟测保留真实 Auth、采集、交付与 Web 回放；临时 profile 删除即清理开发凭据。2026-09-26 用户确认将同一构建的重启恢复纳入 `desktop-replay --recovery` 主链验收，通过真实原生 UI 配置并读取已保存凭据；跨内容变化构建的恢复继续作为原生验收步骤。普通 Keychain 的授权、锁定和升级行为留给独立的平台验收，开发文件路径通过不代表该路径通过，也不新增发行或部署流程。
