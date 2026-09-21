# Heartbeat 桌面客户端

桌面客户端由 AppKit/C#（macOS）与 WinUI 3/C#（Windows）分别承载原生 UI，共享平台无关的 `Heartbeat.Desktop` 运行模块。职责见 [ADR-0017](../../docs/adr/ADR-0017-native-desktop-interfaces.md)。两端原生验收分别进行，C# 编译与共享测试不代表原生 UI 已验收。

## 使用

Mac 需要完整 Xcode、与 Xcode 匹配的 .NET macOS workload（`dotnet workload install macos`），以及 macOS 14 或更新版本。当前宿主以 `net10.0-macos27.0` 使用 .NET 10 对 Xcode 27 的预览支持；首次启动 Xcode 完成许可与组件安装，`xcode-select -p` 应指向 Xcode 的 Developer 目录。SDK 和 Xcode 版本配对以 [.NET macOS 发布说明](https://github.com/dotnet/macios/releases)为准。


```bash
dotnet run --project tools/Heartbeat.Dev -- env up desktop
# 一并启动本地 Web、API 与数据库：
dotnet run --project tools/Heartbeat.Dev -- env up web desktop
```

CLI 复用打包模块生成并打开 `.artifacts/desktop/Heartbeat Dev.app`，不额外启动服务端 Hub。改动代码后先退出开发版再执行命令。

只构建、不启动应用时运行 `dotnet run --project tools/Heartbeat.Dev -- package desktop`。平台、输出目录与前置条件见[本地打包](../../docs/development.md#本地打包)。

可以把 `Heartbeat Dev.app` 拖入本机 Applications 目录再打开。该产物使用本机固定的 `Heartbeat Development` 开发 identity，未做发行签名、公证或自动更新，不用于上线分发。两个平台的原始应用图标位于 `assets/desktop-collector/`；Mac 打包时生成 ICNS，Windows 目前使用系统默认托盘图标。

本地开发包显示为 **Heartbeat Dev**，Bundle ID 为 `com.shenxianovo.heartbeat.desktop.dev`，与旧版 `com.shenxianovo.heartbeat` 不同。开发包使用独立的 `Heartbeat/Desktop` 子目录，不替换 `~/Applications/Heartbeat.app`。

首次打开进入“连接设置”，填写后端、Auth、Web 时间线地址和 API key，点击“验证并保存”，然后开始采集。API key 通过现有 Auth 交换取得 Owner，保存到 macOS 钥匙串，配置 JSON 不包含密钥。后续启动自动恢复采集，离线时本机 Hub 可以接管，连接恢复后再上传。

正常接入不需要打开“钥匙串访问”手动配置权限。Auth 认证与本地保存是两个步骤：认证失败会展示安全的状态说明；保存失败会明确说明账号已验证但连接未保存。凭据写入在后台执行，避免系统授权窗口出现时阻塞客户端界面。系统返回 `-25293` 不能直接判定为“钥匙串未解锁”；不得自动锁定整个登录钥匙串、重置用户凭据库或回退到明文保存。

- 前台应用不需要额外授权。窗口标题和物理输入分别需要辅助功能、输入监控权限；用户点击对应设置按钮时由本进程申请系统权限，再打开系统设置。启动与后台检查不会主动弹权限提示。请为 **Heartbeat Dev** 授权，旧版的权限不会自动继承。若列表没有开发版，可点添加，选择实际运行的 `Heartbeat Dev.app`；系统若要求重新打开应用，按提示操作。
- 暂停时停止原生采集，并尝试把已有快照交给本机 Hub；Hub 继续处理已接管的数据。
- 关闭窗口只隐藏界面，菜单栏或系统托盘可以重新打开；“退出 Heartbeat Dev”才退出客户端。重新打开窗口不会恢复用户已暂停的采集。退出不要求后端在线或网络队列排空。
- “打开时间线”使用默认浏览器打开配置的 Web，由 Web 完成登录和回放。

默认数据目录是 .NET `LocalApplicationData` 下的 `Heartbeat/Desktop`（macOS 为 `~/Library/Application Support/Heartbeat/Desktop`）。配置和 SQLite 队列在此目录；同一目录只允许一个客户端写入，绑定同一 Owner、后端和 Target。独立验证可显式指定目录：

```bash
".artifacts/desktop/Heartbeat Dev.app/Contents/MacOS/Heartbeat.Desktop.Mac" --data-directory /absolute/independent/directory
```

Mac Target 来自 `IOPlatformUUID`；Windows Target 来自 SMBIOS 2.6+ 的系统 UUID，无有效 UUID 时拒绝创建连接。这些身份供各平台 Collector 标识主要观测对象，不引入跨 Collector 的 Device Identity 解析。

### Windows 本地应用

在 Windows 10 2004 或更新系统上，安装 .NET 10 SDK 及 Windows SDK 构建工具后执行：

```powershell
dotnet run --project tools/Heartbeat.Dev -- signing status
dotnet run --project tools/Heartbeat.Dev -- env up desktop
```

`signing setup/status` 明确提示 Windows 开发无需签名且不修改证书库；`env up desktop` 共用打包模块并独立启动应用，命令返回后可关闭终端。只构建使用 `package desktop`。

默认采用宿主机架构；显式指定 ARM64 使用 `--runtime win-arm64`。产物包含 .NET 与 Windows App SDK 运行依赖，是本地未打包的应用目录，尚不提供安装器、发行签名或自动更新。需要保留整个输出目录；不将单个 exe 当作独立产物。

API key 保存到当前 Windows 账号的凭据管理器。窗口关闭后由系统托盘驻留，双击图标重新打开，右键菜单退出。Windows 无 macOS 的辅助功能/输入监控授权页；当前会话、权限隔离与安全桌面可能限制观测，不能把 Available 当作完整性承诺。Windows 原生采集约束见 [Collector README](../Collectors/Heartbeat.Collector.Desktop.Windows/README.md)。

## 实现

| 模块 | 责任 |
| --- | --- |
| `Heartbeat.Desktop` | 连接配置、用户数据目录、组合 Hub 与采集会话、操作串行化、一次性启动恢复、开始/暂停/退出 |
| `Heartbeat.Desktop.Mac` | AppKit UI、菜单栏、应用生命周期、Target 读取、钥匙串和系统设置跳转 |
| `Heartbeat.Desktop.Windows` | WinUI UI、系统托盘、应用生命周期、Target 读取和系统凭据库 |
| `Heartbeat.Collector.Desktop` | 共用桌面观测与投影管线 |
| `Heartbeat.Collector.Desktop.Mac` | Mac 原生采集实现及独立命令行入口 |
| `Heartbeat.Collector.Desktop.Windows` | Win32 前台/标题通知、Raw Input、会话及电源通知 |

客户端同一进程直接调用 Hub 的 SQLite 接管实现；服务器保留 HTTP 宿主。两者使用同一上传循环，各自直接向后端交付。不引入 main 中的旧协议、市场安装系统、版本兼容或退出事务框架。

## 验证

`dotnet run --project tools/Heartbeat.Dev -- scenario desktop-replay` 运行本地打包应用、原生采集、进程内 Hub、隔离后端和真实 Web 回放。首次配置、开始/暂停/退出以及 OIDC 登录需要操作真实界面；场景会输出当前步骤和连接地址。临时 profile 与钥匙串条目在结束时清理。

`Heartbeat.Desktop.Tests` 使用受控观察源与 Auth 响应，验证运行操作连接到真实 SQLite 接管、凭据不落配置文件，配置与开始的串行执行、一次性启动恢复、退出幂等，以及暂停时交接未确认快照；这些是组合测试，不是原生端到端证据。证据边界见[工程验证](../../docs/verification.md)。

Windows 先验收 DevCLI：重复运行 `signing setup/status` 均报告无需签名；`env setup` 完成 Auth 校验后检查 `.env.local` 仅当前用户可访问，再取消一次确认原文件未变；`env up desktop` 返回后关闭终端，确认客户端仍运行；从托盘退出后重新打包启动。

Windows 还需在真实 Windows 桌面验收：首次连接与凭据重读、开始/暂停、关闭窗口与托盘重开、离线接管与恢复交付、退出；原生观察需验证前台切换、标题站稳、左右修饰键、鼠标按钮与双向滚动、锁屏/解锁、会话断开及休眠/唤醒。当前 `desktop-replay` 只覆盖 Mac，Windows 不借用 Mac 场景的通过结论。

运行模块的命令接口可以从多个宿主入口调用；配置、初始化、开始、暂停、退出在同一操作队列中执行。宿主把耗时操作放在后台，并在各自 UI 主线程刷新状态；`IsBusy` 仅供交互提示，运行正确性不依赖按钮是否禁用。权限弹窗和默认浏览器由平台 UI 发起，运行模块不依赖 UI 调度器。共享模块不保存表单输入或窗口可见状态。

## Web 远程启停

Desktop 配置并启动后，在同一 Owner 的 Web `/hubs` 中上报为 Desktop 节点。远程开始/暂停与本地按钮使用同一串行运行入口；暂停只停止采集，Hub 继续联络和交付。退出进程后不会接受远程操作；重新打开仍沿用原有自动开始行为。API 不保存 Desktop 的 API key 或连接设置，详见 [Hub 管理契约](../../docs/hub-management.md)。
