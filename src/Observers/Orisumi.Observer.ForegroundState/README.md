# Orisumi.Observer.ForegroundState

Orisumi.Observer.ForegroundState 在 macOS 上持续采集前台应用，把本机应用和前台应用时段交给 [Runtime](../../Docs/Orisumi.Docs/content/docs/hosting/runtime.mdx) 保存和发送。业务规则见 [Foreground State](../../Docs/Orisumi.Docs/content/docs/observers/foreground-state/index.mdx)。

本文写当前代码的运行方式。目标契约见 [Foreground State](../../Docs/Orisumi.Docs/content/docs/observers/foreground-state/index.mdx)，代码尚未迁移。

进行中的时段放在内存中。切换应用、休眠或停止时，`ForegroundStore.cs` 把本机应用、时段内容和 Observation 一起提交到后端。读取方这时才能读到这一段。

它只引用 `Orisumi.Observer.Sdk` 和 Core，通过标准输入和标准输出与 Runtime 通信，自己不打开 SQLite。程序在主线程运行 AppKit 事件循环，持续读取时才能取得更新后的前台应用。

## 运行

程序由 Runtime 启动，平时随 [macOS 客户端](../../Clients/Orisumi.Mac/README.md)附带。前置条件：macOS 和 `global.json` 指定的 .NET SDK；上传需要运行中的 API，例如用 `orisumi env up api` 启动的本地环境。

不启动客户端时，先构建安装包，再用 Runtime 的 `--run` 运行它：

```sh
orisumi mac build
dotnet run --project src/Observers/Orisumi.Observer.Runtime -- \
	--data-dir /tmp/orisumi-observer-check \
	--backend http://localhost:8080/api/ \
	--bundled .artifacts/mac/Build/Products/Release/Orisumi.app/Contents/Resources/Observers \
	--run foreground-state
```

按 Ctrl+C 停止。Runtime 第一次运行时从 `--bundled` 安装安装包，并为它生成程序实例。

## 启动

程序从 Runtime 的 `init` 读取：

| 字段 | 用途 |
| --- | --- |
| `instanceId` | 程序实例的 EntityId |
| `packageVersion` | 程序实例内容中的 `programVersion` |
| `sharedDataDirectory` | 本机应用登记文件 `local-applications.json` 所在的目录，同一个 Runtime 中的程序实例共用 |
| `settings.intervalMs` | 定时确认的间隔，100 到 60000 毫秒，默认 `1000` |

- 本机应用登记文件损坏时，程序报错并保留原文件；删除后重新分配身份。
- 启动时，程序登记所需的 EntitySchema 和程序实例，再回复 `ready`。
- 读取失败写到标准错误，Runtime 把最近的一条作为采集提示报告给客户端。
- 收到 `stop` 后，当前时段结束于最后一次确认，提交后以 0 退出。初始化或运行失败时以 1 退出。

## 验证

自动测试覆盖本机应用身份、时段与休眠边界、结束时提交、失败后继续采集，以及图标经 API 保存。测试经 `RuntimeObserverSession` 调用内存 HTTP 后端或真实 API，不调用 macOS 原生接口。Case 见文档站 Testing 分区。

```sh
orisumi verify checks --base HEAD --quality
```

原生读取需要在 macOS 上实际运行：按[运行](#运行)的命令启动，切换应用，然后检查：

1. 同一应用连续确认时，尚未结束的时段不能从 API 读到。
2. 切换应用或停用后，旧时段能从 API 读到，结束边界是最后一次确认。
3. 已结束的时段可以用 `GET /api/entities/{observationId}` 读到。
4. 再次运行，程序实例和本机应用的 EntityId 不变，Observation 是新的。
5. 前台时段的 `icon` 有值，用 `GET /api/contents/{digest}` 读回的是 1024×1024 的 PNG，长度等于 `size`。同一个应用的下一段时段读到相同的摘要。

真实休眠需要另外验证。自动测试只模拟计时差和采集空白。
