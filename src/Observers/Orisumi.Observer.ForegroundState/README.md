# Orisumi.Observer.ForegroundState

Orisumi.Observer.ForegroundState 在 macOS 上持续采集前台应用，通过 [.NET SDK](../Orisumi.Observer.Sdk/README.md) 把本机应用和已结束的前台应用时段上传到后端。业务规则见 [Foreground State](../../Docs/Orisumi.Docs/content/docs/observers/application-activity/index.mdx)。

程序在主线程运行 AppKit 事件循环，持续读取时才能取得更新后的前台应用。

## 运行

前置条件：macOS 和 `global.json` 指定的 .NET SDK。上传需要运行中的 API，例如用 `orisumi env up api` 启动的本地环境。

在仓库根目录运行：

```sh
dotnet run --project src/Observers/Orisumi.Observer.ForegroundState
```

按 Ctrl+C 停止。只采集指定次数：

```sh
dotnet run --project src/Observers/Orisumi.Observer.ForegroundState -- --samples 10
```

## 参数

| 参数 | 默认值 | 含义 |
| --- | --- | --- |
| `--identity-file` | `~/Library/Application Support/Orisumi/observers/macos-system/identity.json` | 程序实例的身份文件 |
| `--applications-file` | `~/Library/Application Support/Orisumi/local-applications.json` | 本机应用登记文件 |
| `--state-file` | 身份文件路径加 `.sqlite` | SDK 的 SQLite 文件：草稿和待发送的版本 |
| `--backend` | `http://localhost:8080/api/` | API 的地址 |
| `--interval-ms` | `1000` | 定时确认的间隔，100 到 60000 毫秒 |
| `--samples` | `0` | 采样次数。`0` 表示持续运行。读取失败也计一次 |

## 文件

| 文件 | 规则 |
| --- | --- |
| 身份文件 | 一个文件对应一个程序实例。启动另一个实例时使用新的身份文件 |
| 状态文件 | 绑定一个程序实例，不能交给另一个实例使用 |
| 本机应用登记文件 | 同一设备上的实例共用。另一台设备使用自己的文件，不复制已有文件 |

- 同一个身份文件或状态文件只允许一个进程使用。`.lock` 文件由操作系统锁定，进程退出后释放。留下的空锁文件不影响重启。
- 文件损坏时，程序报错并保留原文件。
- 删除文件后，程序重新分配文件中管理的身份。

## 输出

标准输出每行一个 JSON 对象，`type` 区分两种事件。[macOS 客户端](../../Clients/Orisumi.Mac/README.md)读取这些行显示状态。

| `type` | 何时写 | 其余字段 |
| --- | --- | --- |
| `checkpoint` | 每次本地确认 | `observerId`、`localApplicationId`、`identifiers`（本机应用的标识，同本机应用的内容）、`contentId`、`observationId`、`startAt`、`endAt`、`name` |
| `delivery` | 发送状态变化时 | `pending`、`drafts`、`saved`；`rejection` 为 `null` 或 `{rule, detail}`；`error` 为 `null` 或原因 |

时段结束时交给 SDK 发送。收到 SIGINT 时，当前时段结束于最后一次本地确认并保存在本地，程序以 130 退出，下次启动后发送。读取失败和退出原因写到标准错误。

| 退出码 | 含义 |
| --- | --- |
| 0 | 指定次数的采样结束，本地状态已保存 |
| 1 | 本地文件、运行环境或后台任务失败 |
| 2 | 参数错误 |
| 130 | 收到停止请求，尚未确认的版本留在状态文件中 |

## 验证

自动测试覆盖本机应用身份、时段边界、采集事件、休眠边界、本地事务恢复，以及经 API 上传和本机应用被删除后的恢复。测试不调用 macOS 原生接口。Case 见文档站的 Testing 分区。

```sh
orisumi verify changed --base HEAD --quality
```

原生读取需要在 macOS 上实际运行。使用临时文件：

```sh
dotnet run --project src/Observers/Orisumi.Observer.ForegroundState -- \
	--identity-file /tmp/orisumi-observer-check/identity.json \
	--applications-file /tmp/orisumi-observer-check/applications.json \
	--samples 10
```

运行期间切换应用，然后检查：

1. 同一应用的连续确认沿用同一个 Observation ID。
2. 切换应用后出现新的 Observation。
3. 已结束的时段可以用 `GET /api/entities/{observationId}` 读到。
4. 再次运行，本机应用的 EntityId 不变，Observation ID 是新的。

真实休眠需要另外验证。自动测试只模拟计时差和采集空白。
