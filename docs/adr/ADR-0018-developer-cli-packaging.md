# ADR-0018：Developer CLI 统一命令定义与本地桌面打包

## 状态：已接受

## 日期：2026-09-20

本地打包原由 Bash 与 PowerShell 脚本维护，环境启动和桌面回放场景依赖脚本路径；DevCLI 各组又分别解析参数、维护帮助文本。用户确认把打包迁入 DevCLI，引入 System.CommandLine，并按功能组织实现，保留现有浅层命令分组。

各功能模块定义自己的命令树与选项，根入口只组合；System.CommandLine 负责语法解析和帮助，执行逻辑接收类型化参数。开发工具内部直接复用实现，不通过命令字符串或再次启动 CLI 复用功能。

`DesktopPackager` 统一负责本地桌面发布编排、图标、签名、临时目录清理与具名产物替换；`package desktop`、`env up desktop` 和 `scenario desktop-replay` 共用它。平台 SDK 继续负责各自的应用结构，macOS 构建要求 macOS，Windows 构建要求 Windows。本决策细化 ADR-0017 的平台打包入口，不改变原生宿主职责。

本决策最初删除打包脚本、保留薄 CLI 启动脚本；随后 [ADR-0021](ADR-0021-cross-platform-development-commands.md) 统一直接运行 .NET CLI 并删除整个 `scripts/`。本决策当时保留 ad-hoc 开发签名；macOS 开发签名随后由 [ADR-0020](ADR-0020-stable-macos-development-signing.md) 改为固定本地身份。Windows 继续使用 self-contained 目录形式；本次不引入发行签名、安装器、更新或部署。统一入口减少参数与行为副本，但平台工具和平台验收仍需分别维护。

- [DevCLI 结构](../../tools/Heartbeat.Dev/README.md)
- [本地打包契约](../development.md#本地打包)
- [共享打包实现](../../tools/Heartbeat.Dev/Packaging/DesktopPackager.cs)
