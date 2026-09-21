# 原生桌面 UI

已确认 WinUI 3/C# 与 AppKit/C# 各自承载 UI，共用平台无关 DesktopRuntime；职责以 ADR-0017 为准。Mac 原生验收因 Xcode 暂缺推迟，用户要求先完成其他工作，Windows 实现同步推进。

实现已包含：共享操作串行化和一次性启动恢复；AppKit 窗口/菜单栏；WinUI 窗口/系统托盘与系统凭据；Windows 原生观测；删除 Avalonia；两端本地打包入口；测试和文档同步。

验证基点为 a03edc5921cca2846d989a98729c78d8e7b77644（原有 LOC 工具修改由其他任务提交）。本任务不修改 docs/research/ 中既有未跟踪调研。

待完成：
- 安装完整 Xcode 后构建并验收 AppKit 包，执行 desktop-replay 和客户端 README 的 Mac 人工步骤。
- 在 Windows 上执行 dotnet run --project tools/Heartbeat.Dev -- package desktop 并验收 WinUI、凭据和原生采集。
- 平台原生验证全部完成后删除本目录。

本机 .NET macOS workload 安装在 .artifacts/toolchains/dotnet，不修改系统 SDK。验证时使用 PATH="$PWD/.artifacts/toolchains/dotnet:$PATH"。Xcode 需与 workload 配对，当前为 10.0.302.1 / macOS SDK 26.5；升级后先检查安装版本再构建。

C# 结构分析已分开普通项目构建与原生宿主的托管编译，仍对每套宿主执行分析器；该步骤不链接原生应用，不证明原生运行成功。原生验证阻塞记录见 issues/。
