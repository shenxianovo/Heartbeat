# Heartbeat 桌面客户端

桌面客户端由 macOS AppKit/C# 与 Windows WinUI 3/C# 分别承载原生 UI，共用平台无关的 `Heartbeat.Desktop` 运行模块。窗口用于配置和查看采集状态；关闭窗口后应用继续在菜单栏或系统托盘运行。责任边界见 [ADR-0017](../../docs/adr/ADR-0017-native-desktop-interfaces.md)。

## 启动

macOS 需要 macOS 14+、完整 Xcode 和匹配的 .NET macOS workload；Windows 需要 .NET 10 SDK 与 Windows SDK 构建工具。

```bash
dotnet run --project tools/Heartbeat.Dev -- env up desktop
dotnet run --project tools/Heartbeat.Dev -- env up web desktop
```

修改客户端代码后先退出已运行的 Heartbeat Dev，再执行命令。只打包不启动时运行：

```bash
dotnet run --project tools/Heartbeat.Dev -- package desktop
```

支持的平台、RID、输出目录和 macOS 签名准备见[本地打包](../../docs/development.md#本地打包)。开发产物没有发行签名、公证、安装器或自动更新，不用于上线分发。

## 使用与权限

首次打开时填写后端、Auth、Web 地址和 API key，验证保存后开始采集。后续启动会恢复已保存连接及自动采集；离线时进程内 Hub 继续接管，连接恢复后上传。

- 前台应用不需要额外权限。
- macOS 窗口标题需要 Accessibility，物理输入需要 Input Monitoring；权限按钮由客户端显式发起系统请求。
- 暂停会停止原生采集并尝试交接已有快照，Hub 继续上传已接管数据。
- 关闭窗口只隐藏界面；菜单栏或系统托盘可以重新打开，“退出 Heartbeat Dev”才结束进程。
- “打开时间线”交给默认浏览器，由 Web 完成登录和回放。

能力可用只表示观察器可工作，不承诺记录完整；账号验证成功也不表示后端始终在线。

## 平台差异

| 项目 | macOS | Windows |
| --- | --- | --- |
| 原生 UI | AppKit、菜单栏 | WinUI 3、系统托盘 |
| 开发包 | `Heartbeat Dev.app` | 完整 self-contained 目录 |
| 开发凭据 | `Heartbeat/DesktopDev/api-key` 受限文件 | 当前账号的 Credential Manager |
| 普通凭据 | Keychain | Credential Manager |
| Target | `IOPlatformUUID` | SMBIOS 2.6+ 系统 UUID |
| 采集权限 | Accessibility、Input Monitoring | 受当前会话、安全桌面和企业策略限制 |

macOS 开发 Bundle ID 为 `com.shenxianovo.heartbeat.desktop.dev`，使用独立 profile；普通 macOS 构建和 Windows 使用 `Heartbeat/Desktop`。配置和 Hub SQLite 位于对应的 `LocalApplicationData` 目录，同一目录只允许一个客户端写入。

开发凭据文件只提供系统用户级权限，不等价于 Keychain 的应用隔离。存储选择按运行时 Bundle ID 决定，不按 Debug/Release，也不会在 Keychain 失败后降级。完整决策见 [ADR-0022](../../docs/adr/ADR-0022-macos-development-credentials.md)。

## 实现责任

| 模块 | 责任 |
| --- | --- |
| `Heartbeat.Desktop` | 连接、数据目录、Hub 与采集会话组合、操作串行化、启动恢复和运行状态 |
| `Heartbeat.Desktop.Mac` | AppKit、菜单栏、生命周期、Target、Keychain 和系统设置跳转 |
| `Heartbeat.Desktop.Windows` | WinUI、系统托盘、生命周期、Target 和 Credential Manager |
| `Heartbeat.Collector.Desktop` | 共用桌面观测与 Record 投影 |
| 平台 Collector | 原生通知、系统时钟和平台读数 |

客户端直接调用进程内 Hub 的 SQLite 接管实现；服务器保留 HTTP Hub。共享运行模块不依赖 AppKit、WinUI 或 UI 调度器，平台宿主负责系统交互和主线程更新。

## 验证

```bash
dotnet run --project tools/Heartbeat.Dev -- scenario desktop-replay
```

该场景自动操作真实 UI 完成首次配置与采集，再验证同一 Record 的 Web 回放。增加 `--recovery` 验证离线接管、强制退出、保存凭据重启和恢复交付；增加 `--interactive-login` 单独验收真实 OIDC 登录。默认浏览器使用真实 Auth 签发的短期令牌。场景不覆盖普通构建 Keychain、Windows、发行或系统权限交互。证据边界见[工程验证](../../docs/verification.md#桌面应用到真实-web-回放)。

原生人工验收按风险选择：

- macOS：首次连接、开始/暂停/退出、窗口隐藏与重开、权限撤销与恢复、明暗外观、减少动态效果、键盘与 VoiceOver；跨内容变化重新打包后确认 TCC 权限和开发凭据恢复。
- Windows：首次连接与凭据重读、窗口与托盘生命周期、离线接管与恢复交付，以及前台、标题、输入、锁屏、会话和休眠通知。

共享测试或跨平台托管编译不代表原生 UI 已验收；静态截图也不证明动画和辅助技术行为。

## Web 远程启停

客户端连接后会在同一 Owner 的 Web `/hubs` 中显示为 Desktop 节点。远程开始/暂停与本地按钮使用同一串行入口；暂停只停止采集，Hub 继续联络和交付。进程退出后不再接受远程操作。行为契约见 [Hub 管理](../../docs/hub-management.md)。

## 收发反馈

交付区显示最近 60 秒的接收、发送、确认曲线，纵轴为每秒 Record 快照数，重试和续期会重复计入。AppKit 和 WinUI 直接读取进程内 Hub 的时间桶，不依赖 Web 或 API。窗口不可见时停止连续绘制；系统减少动态时只做每秒更新。发送不表示交付完成。

原生检查：保持采集页可见，观察接收、发送与稍后的确认波峰；断开后端不应产生新增成功确认，恢复后才能确认。暂停采集时积压上传仍可出现波峰；重开窗口显示当前窗口，重启 Hub 清空旧运行的曲线。永久失败标为需处理。规则见 [收发活动](../../docs/adr/ADR-0024-delivery-activity-is-ephemeral.md)。
