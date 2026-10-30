# Orisumi.Mac

Orisumi.Mac 是常驻菜单栏的 macOS 客户端。它启动 [Runtime](../../Docs/Orisumi.Docs/content/docs/hosting/runtime.mdx)，通过 Runtime 安装和运行本机的 Observer，并展示它们的状态和数据。客户端的责任见 [ADR · 设备端客户端](../../Docs/Orisumi.Docs/content/docs/adr/device-client.mdx)，托管规则见 [Clients · Device Clients](../../Docs/Orisumi.Docs/content/docs/clients/device.mdx)，界面和实现进度见 [macOS](../../Docs/Orisumi.Docs/content/docs/clients/macos.mdx)。

## 结构

| 位置 | 内容 |
| --- | --- |
| `Orisumi/` | SwiftUI 应用：菜单、窗口、设置和应用生命周期 |
| `OrisumiClient/` | Swift 包：Runtime 的进程和管道、管理与状态、经 Runtime 读取数据。单元测试在这里 |
| `Scripts/publish-runtime.sh` | Xcode 构建阶段：用本检出的 `orisumi observers pack` 构建并签名 Runtime 和附带的 Observer 的安装包，复制进应用 |

`Assets.xcassets` 中的图标由 `src/Clients/Orisumi.Web/public/mascots/macos.png` 缩放而成。Runtime 是自包含的单文件 .NET 程序。前台状态 Observer 的安装包是 `foreground-state-<版本>-<平台>.zip`，格式见 [Packages](../../Docs/Orisumi.Docs/content/docs/hosting/packages.mdx)，版本取自它的 csproj。Runtime 第一次启动时安装它，之后和其他 Observer 一样管理。

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

菜单、窗口和设置的行为见 [macOS](../../Docs/Orisumi.Docs/content/docs/clients/macos.mdx)。设置窗口用 ⌘, 打开。

## 开发

设置保存在 UserDefaults（`com.shenxianovo.orisumi`）中。外观存为 `appearance`（`system`、`light`、`dark`），只改变 Orisumi 自己，开发时可以用 `-appearance dark` 临时指定。下面几项没有界面，用启动参数临时指定：

| 参数 | 作用 |
| --- | --- |
| `-dataDirectory <目录>` | Runtime 的数据目录，也就是另一组程序实例 |
| `-runtime <路径>` | 使用另一个 Runtime 可执行文件 |
| `-catalog <URL>` | 官方目录的地址。不指定时只列出附带的安装包 |
| `-showWindow YES` | 启动时打开窗口 |

Runtime 意外退出后，客户端依次等待 1、2、4 秒重启它，仍然失败时显示错误和“重新启动 Runtime”。客户端收到 SIGTERM 时按“退出 Orisumi”处理，先关闭打开的弹窗，`orisumi mac stop` 就是这样停止客户端的。

## 验证

```sh
swift test --package-path src/Clients/Orisumi.Mac/OrisumiClient
orisumi verify checks
```

`orisumi verify checks` 在 `src/Clients/Orisumi.Mac/` 有改动时运行 `swift test`，并构建 Debug 版。单元测试用 shell 脚本代替 Runtime，用替身代替管道的响应。

`RuntimeIntegrationTests` 让同一套 Swift 代码驱动真实的 Runtime 和 API：附带包安装后采集，经 Runtime 读到今天的时段，停用、启用、卸载、重装和重启 Runtime 后沿用同一个程序实例。它只在设置了 `ORISUMI_APP` 时运行：

```sh
orisumi mac build --configuration debug
ORISUMI_APP=$PWD/.artifacts/mac/Build/Products/Debug/Orisumi.app ORISUMI_BACKEND=http://localhost:8080/api/ \
  swift test --package-path src/Clients/Orisumi.Mac/OrisumiClient --filter RuntimeIntegrationTests
```

菜单、窗口的操作需要实际运行：用 `orisumi env up api web --temp` 和 `orisumi mac run --window` 启动，切换几次应用；停用再启用、卸载再重装前台状态 Observer，确认它沿用原来的身份；然后在窗口和 Web 控制台中读到已结束的时段。
