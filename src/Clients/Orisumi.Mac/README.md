# Orisumi.Mac

Orisumi.Mac 是常驻菜单栏的 macOS 客户端，托管本机的桌面 Observer。第一版托管[前台状态 Observer](../../Observers/Orisumi.Observer.ForegroundState/README.md) 的应用时段。客户端的责任见 [ADR · 桌面客户端](../../Docs/Orisumi.Docs/content/docs/adr/desktop-client.mdx)，托管规则见 [Desktop · 客户端](../../Docs/Orisumi.Docs/content/docs/observers/desktop/index.mdx#client)。

## 结构

| 位置 | 内容 |
| --- | --- |
| `Orisumi/` | SwiftUI 应用：菜单、窗口、设置和应用生命周期；`Assets.xcassets` 中的图标由 `src/Clients/Orisumi.Web/public/mascots/macos.png` 缩放而成 |
| `OrisumiClient/` | Swift 包：解析 helper 的输出、监管 helper 进程、从 API 读取今天的时段，以及菜单和窗口共用的状态。单元测试在这里 |
| `Scripts/publish-helper.sh` | Xcode 构建阶段：发布前台状态 Observer，放进 `Orisumi.app/Contents/Helpers/` 并签名 |

helper 是自包含的单文件 .NET 程序。客户端启动它时只传 `--backend`，身份文件和登记文件使用命令行程序的默认路径，所以已有的程序实例在客户端中继续运行。

## 构建与运行

前置条件：Xcode、`global.json` 指定的 .NET SDK，以及钥匙串中名为 `Orisumi Development` 的代码签名身份。macOS 按签名和 bundle identifier（`com.shenxianovo.orisumi`）记住系统权限和登录项，签名身份不变，重新构建后这些设置就还在。

开发时用 [Orisumi.Dev](../../../tools/Orisumi.Dev/README.md#macos-客户端) 构建，并连接本检出目录的环境：

```sh
orisumi env up api web --temp
orisumi mac run --build --window
orisumi mac stop
```

`orisumi mac run` 为每个数据卷使用单独的程序实例，数据在 `.artifacts/mac/instances/<卷名>/`。构建产物在 `.artifacts/mac/Build/Products/`，.NET 的构建输出也在其中，主检出目录中运行的 AppHost 不会因此重启。也可以用 Xcode 打开 `Orisumi.xcodeproj` 运行。

日常使用时，把 Release 版的 `Orisumi.app` 复制到 `/Applications`，再在设置中打开“登录时启动”。这时客户端使用默认的程序实例。

## 使用

客户端平时只在菜单栏显示吉祥物图标：采集时是彩色，暂停时变灰，helper 意外退出时右下角有红点。

菜单显示采集状态、当前的前台应用、待发送和已保存的数量，以及被拒绝的原因。

| 菜单项 | 作用 |
| --- | --- |
| 暂停采集 | 停止 helper。当前时段结束于最后一次确认，待发送的版本留在本地 |
| 继续采集 | 启动 helper，补发待发送的版本 |
| 重新启动 | helper 意外退出后再次启动。菜单显示它最后一行标准错误 |
| 打开 Orisumi | 打开窗口的“状态”页 |
| 打开 Web 控制台 | 用浏览器打开设置中的 Web 控制台地址 |
| 设置… | 打开窗口的“设置”页：API 地址、Web 控制台地址、登录时启动。修改 API 地址后重新启动采集 |
| 退出 Orisumi | 先停止 helper，再退出 |

窗口打开时，客户端出现在 Dock 中，关闭后只留在菜单栏。窗口是横向的，最小宽度保证图表和图例并排。“状态”页的顶部是采集状态、今天的前台总时长和发送状态，下面两块各自滚动，页面本身不滚动：

- “今天”：左边是今天 24 个小时的柱状图，每小时的前台分钟数按应用堆叠。今天时长最长的 6 个应用各有一种颜色，其余是灰色。指向某个小时，这一列铺上浅灰底，旁边弹出这个小时各应用的时长和合计，提示框在柱子之上。右边是应用列表，显示总时长、时段数、最长一段和占比；指向某个应用，图表中的其他应用变淡；点击某个应用，下方只显示它的时段，再点一次或点“只看 …”取消。
- “时段”：按时间倒序，进行中的在最前面。

今天的记录从 API 读取：`GET /observations` 列出今天的前台应用时段，客户端只保留 Observer 是本机程序实例的，再补上 helper 报告的、还没发送的时段。程序实例取自 helper 的 `checkpoint` 行，读过的版本按 VersionId 缓存。客户端在拿到程序实例、已保存的数量变化时重新读取。API 不可用时，页面只显示本地的时段。其他日期在 Web 控制台中查看。

## 开发

设置保存在 UserDefaults（`com.shenxianovo.orisumi`）中。下面几项没有界面，用启动参数临时指定：

| 参数 | 作用 |
| --- | --- |
| `-dataDirectory <目录>` | helper 使用这个目录中的身份文件和登记文件，也就是另一个程序实例 |
| `-helper <路径>` | 使用另一个 helper 可执行文件，例如逐行输出示例事件的脚本 |
| `-showWindow YES` | 启动时打开窗口 |

客户端收到 SIGTERM 时按“退出 Orisumi”处理：先停止 helper，再退出。`orisumi mac stop` 就是这样停止客户端的。

## 验证

```sh
swift test --package-path src/Clients/Orisumi.Mac/OrisumiClient
orisumi verify checks
```

`orisumi verify checks` 在 `src/Clients/Orisumi.Mac/` 有改动时运行 `swift test`，并构建 Debug 版。单元测试用 shell 脚本代替 helper，用替身代替 API，覆盖事件解析、停止时发送 SIGINT、helper 意外退出和重新启动、读取今天的时段（分页、按程序实例筛选、跳过已删除的内容、版本缓存），以及时段的合并、按应用汇总、按小时拆分和时间范围。菜单、窗口和与真实 helper 的配合需要实际运行：用 `orisumi env up api web --temp` 和 `orisumi mac run --window` 启动，切换几次应用，然后在窗口和 Web 控制台中读到已结束的时段。
