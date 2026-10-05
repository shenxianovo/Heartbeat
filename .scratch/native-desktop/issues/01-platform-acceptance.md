Status: ready-for-human

# 补齐原生桌面验收环境

架构与代码参考 ADR-0017 和 src/Desktop/README.md。当前已完成自动测试与两端托管编译；原生应用运行尚未验收。

## macOS

2026-09-21 已安装 Xcode 27.0，`xcodebuild -checkFirstLaunchStatus` 通过。Mac 宿主显式目标改为 `net10.0-macos27.0`，使用 .NET 10 workload 内置的 Xcode 27 预览 SDK pack；`./scripts/heartbeat-dev package desktop` 已完成发布、ad-hoc 签名和签名校验。仍需继续真实配置、钥匙串、暂停、窗口重开、退出及 Web 回放验收。

2026-09-20 执行 `PATH="$PWD/.artifacts/toolchains/dotnet:$PATH" ./scripts/heartbeat-dev scenario desktop-replay`，在打包阶段失败，证据为 `.artifacts/verification/20260920T081939Z-scenario-desktop-replay-9d9a0798a4104fc6b14659cca5298f25/manifest.json` 和 `desktop-build.log`。隔离 Docker 环境已清理。

当前 Xcode 与 workload 已能完成应用构建；下一步由 Agent 继续真实配置/钥匙串、暂停/窗口重开、退出及 Web 回放验收。

## Windows

当前执行主机为 macOS。WinUI 的托管代码已编译，但原生 manifest 合并工具 mt.exe 无法在本机执行。

需要 Windows 10 2004+ 或 Windows 11、.NET 10 SDK 和 Windows SDK；先运行 `dotnet run --project tools/Heartbeat.Dev -- signing setup` / `signing status`，确认报告无需签名且不改证书库；通过 `env up desktop` 验收打包、独立启动与终端退出后继续运行。需使用本地服务端 Hub 时运行 `env setup`，验收 Auth 校验、隐藏输入、Windows 文件 ACL，以及取消后原 `.env.local` 不变。再按客户端 README 验收凭据、托盘、Raw Input、前台窗口、会话与电源通知。纯翻译/解析测试和 Mac 场景均不能证明这些行为。

## Comments

- 用户要求先完成所有不依赖 Xcode 的工作。此次不部署，也不建设 CI。

- 2026-09-21 用户确认 Windows 不创建开发签名证书；本轮迁移 setup 到跨平台 DevCLI，Windows 原生执行由用户之后在 Win 电脑验收。命令和受控测试通过不替代此验收。

- 开发入口已统一为 `dotnet run --project tools/Heartbeat.Dev -- <子命令>`；上方带日期的旧脚本命令仅记录历史执行，重跑使用直接入口。
