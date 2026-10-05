# ADR-0020：macOS 开发包使用稳定的本地签名身份

## 状态：已接受

## 日期：2026-09-21

macOS 的 Accessibility 与 Input Monitoring 授权按代码身份识别应用。ADR-0018 保留的 ad-hoc 签名把 designated requirement 绑定到当前构建的 CDHash；`env up desktop` 又会在每次启动前重新打包，因此代码变化后系统无法把新包识别为已经授权的 Heartbeat Dev。

Heartbeat Dev 固定使用 Bundle ID `com.shenxianovo.heartbeat.desktop.dev`，并由 `DesktopPackager` 使用登录钥匙串中名为 `Heartbeat Development` 的持久 Code Signing identity 签署所有原生动态库和外层应用包。打包前必须确认该 identity 存在；缺失时明确失败，不回退到 ad-hoc 签名。DevCLI 的 `signing setup` 负责首次创建十年有效的自签名 identity，并只为当前用户添加 Code Signing 信任；`signing status` 只读检查。证书与私钥的权威位置是当前用户的登录钥匙串，所有分支和 worktree 共用，不随仓库清理或打包重新生成。正常打包按唯一证书指纹签名；缺失、无效或重名都不得静默替换身份。

固定 identity 让同一台开发机器上的后续构建满足相同的 certificate requirement，从而避免因重建改变身份而丢失 TCC 授权。首次切换到新 Bundle ID / 签名身份时仍需重新授予，系统权限交互须实机验收。更换或删除 certificate 会再次改变代码身份。该证书只用于本地开发，不代表发行信任，不替代 Developer ID、公证、Hardened Runtime 或生产 entitlements；不同开发者机器之间也不共享 TCC 授权。

本决策细化 ADR-0018 已确定的签名责任，并替代其中保留 ad-hoc macOS 开发签名的部分。未来生产分发不在本决策范围内；Windows 共用命令但无需开发签名，见 [ADR-0021](ADR-0021-cross-platform-development-commands.md)。

- [本地打包与签名准备](../development.md#macos-开发签名)
- [Developer CLI 打包职责](ADR-0018-developer-cli-packaging.md)

开发身份由 DevCLI 显式传入 `HeartbeatDevelopmentBuild=true` 启用；它与 Debug/Release 编译优化配置独立。普通项目默认身份为 `Heartbeat` / `com.shenxianovo.heartbeat.desktop`，不依赖本地开发证书。开发包可使用 Release 优化；本决策不建设正式发行流程。开发打包的身份字段由项目属性生成，不在 Info.plist 维护第二份。
