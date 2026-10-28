# Orisumi.Mac

Orisumi.Mac 是常驻菜单栏的 macOS 客户端。它启动 [Runtime](../../Docs/Orisumi.Docs/content/docs/clients/runtime.mdx)，通过 Runtime 安装和运行本机的 Observer，并展示它们的状态和数据。客户端的责任见 [ADR · 设备端客户端](../../Docs/Orisumi.Docs/content/docs/adr/device-client.mdx)，托管规则见 [Clients · 设备端客户端](../../Docs/Orisumi.Docs/content/docs/clients/index.mdx#device)，界面和实现进度见 [macOS](../../Docs/Orisumi.Docs/content/docs/clients/macos.mdx)。

## 结构

| 位置 | 内容 |
| --- | --- |
| `Orisumi/` | SwiftUI 应用：菜单、窗口的侧栏和 Observer 详情、添加 Observer、前台应用的专用视图、设置和应用生命周期；`Assets.xcassets` 中的图标由 `src/Clients/Orisumi.Web/public/mascots/macos.png` 缩放而成 |
| `OrisumiClient/` | Swift 包：Runtime 进程和管道（`RuntimeConnection`）、管理与状态（`RuntimeModel`）、经 Runtime 读取 Observation（`ForegroundReader`、`RecentReader`），以及今天的统计。单元测试在这里 |
| `Scripts/publish-runtime.sh` | Xcode 构建阶段：发布 Runtime 到 `Contents/Helpers/Runtime/`；把前台状态 Observer 打成安装包，放进 `Contents/Resources/Observers/`；签名两者 |

Runtime 是自包含的单文件 .NET 程序。前台状态 Observer 的安装包是 `foreground-state-<版本>-<平台>.zip`，格式见 [Packages](../../Docs/Orisumi.Docs/content/docs/clients/packages.mdx)，版本取自它的 csproj。Runtime 第一次启动时安装它，之后和其他 Observer 一样管理。

## 构建与运行

前置条件：Xcode、`global.json` 指定的 .NET SDK，以及钥匙串中名为 `Orisumi Development` 的代码签名身份。macOS 按签名和 bundle identifier（`com.shenxianovo.orisumi`）记住系统权限和登录项，签名身份不变，重新构建后这些设置就还在。

开发时用 [Orisumi.Dev](../../../tools/Orisumi.Dev/README.md#macos-客户端) 构建，并连接本检出目录的环境：

```sh
orisumi env up api web --temp
orisumi mac run --build --window
orisumi mac stop
```

`orisumi mac run` 为每个数据卷使用单独的 Runtime 数据目录，在 `.artifacts/mac/instances/<卷名>/`。构建产物在 `.artifacts/mac/Build/Products/`，.NET 的构建输出也在其中，主检出目录中运行的 AppHost 不会因此重启。也可以用 Xcode 打开 `Orisumi.xcodeproj` 运行。

日常使用时，把 Release 版的 `Orisumi.app` 复制到 `/Applications`，再在设置中打开“登录时启动”。这时 Runtime 的数据目录是 `~/Library/Application Support/Orisumi/Runtime/`。

## 使用

客户端平时只在菜单栏显示吉祥物图标：有 Observer 在采集时是彩色，否则变灰；Runtime 或某个 Observer 故障、发送被暂停时，右下角有红点。

菜单显示 Runtime 的状态、每个已安装的 Observer（勾选即启用）、待发送和已保存的总数，以及发送被暂停的 Observer。

| 菜单项 | 作用 |
| --- | --- |
| Observer 名称 | 启用或停用它。停用时当前进行中的事实按它的规则结束，待发送的版本继续发送 |
| 重新启动 Runtime | Runtime 连续意外退出、客户端停止重启后出现 |
| 打开 Orisumi | 打开窗口 |
| 打开 Web 控制台 | 用浏览器打开设置中的 Web 控制台地址 |
| 设置… | 打开窗口的设置：API 地址、Web 控制台地址、本机数据目录、登录时启动。修改 API 地址后重新启动 Runtime |
| 退出 Orisumi | Runtime 先停止所有 Observer，再停止发送，然后客户端退出 |

窗口打开时，客户端出现在 Dock 中，关闭后只留在菜单栏。

- 侧栏列出 Observer，圆点表示进程状态：绿色在采集，橙色在启动、停止或等待重启，红色故障。已卸载但还有待发送数据的列在“已卸载”下。底部的“添加 Observer…”打开可安装的列表。
- 详情的顶部是名称、版本、进程和发送状态，右侧是启用开关、重试、更新和“卸载…”。错误、被拒绝的版本和采集提示显示在下面。
- 前台状态 Observer 的详情是今天的前台应用：左边是今天 24 个小时的柱状图，每小时的前台分钟数按应用堆叠，时长最长的 6 个应用各有一种颜色。指向某个小时，旁边弹出这个小时各应用的时长。右边是应用列表；指向某个应用，图表中的其他应用变淡；点击某个应用，下方只显示它的时段。时长算到最近一次确认。
- 其他 Observer 的详情列出最近 7 天的 Observation 和它们的本机状态。

数据经 Runtime 读取：Runtime 合并 API 中已保存的和本地还没发送的部分。窗口可见时每 2 秒读取一次；窗口关闭后，客户端每 15 秒读取一次 Observer 的状态，用于菜单栏图标。API 不可用时，视图只显示本机的数据，并标明可能不完整。其他日期在 Web 控制台中查看。

## 开发

设置保存在 UserDefaults（`com.shenxianovo.orisumi`）中。下面几项没有界面，用启动参数临时指定：

| 参数 | 作用 |
| --- | --- |
| `-dataDirectory <目录>` | Runtime 的数据目录，也就是另一组程序实例 |
| `-runtime <路径>` | 使用另一个 Runtime 可执行文件 |
| `-catalog <URL>` | 官方目录的地址。不指定时只列出附带的安装包 |
| `-showWindow YES` | 启动时打开窗口 |

Runtime 意外退出后，客户端依次等待 1、2、4 秒重启它，仍然失败时显示错误和“重新启动 Runtime”。客户端收到 SIGTERM 时按“退出 Orisumi”处理，`orisumi mac stop` 就是这样停止客户端的。

## 验证

```sh
swift test --package-path src/Clients/Orisumi.Mac/OrisumiClient
orisumi verify checks
```

`orisumi verify checks` 在 `src/Clients/Orisumi.Mac/` 有改动时运行 `swift test`，并构建 Debug 版。单元测试用 shell 脚本代替 Runtime 检查管道（请求与响应的对应、错误、意外退出、不响应 `shutdown` 时关闭标准输入），用替身检查管理请求、Runtime 的重启次数，以及经 Runtime 读取今天的时段（分页、游标过期后重读、缺失的内容不再显示、依赖读取出错时标为不完整）和统计。

`RuntimeIntegrationTests` 让同一套 Swift 代码驱动真实的 Runtime 和 API：附带包安装后采集，经 Runtime 读到今天的时段，停用、启用、卸载、重装和重启 Runtime 后沿用同一个程序实例。它只在设置了 `ORISUMI_APP` 时运行：

```sh
orisumi mac build --configuration debug
ORISUMI_APP=$PWD/.artifacts/mac/Build/Products/Debug/Orisumi.app ORISUMI_BACKEND=http://localhost:8080/api/ \
  swift test --package-path src/Clients/Orisumi.Mac/OrisumiClient --filter RuntimeIntegrationTests
```

菜单、窗口的操作需要实际运行：用 `orisumi env up api web --temp` 和 `orisumi mac run --window` 启动，切换几次应用；停用再启用、卸载再重装前台状态 Observer，确认它沿用原来的身份；然后在窗口和 Web 控制台中读到已结束的时段。
