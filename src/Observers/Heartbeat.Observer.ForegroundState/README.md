# Heartbeat.Observer.ForegroundState

Heartbeat.Observer.ForegroundState 在 macOS 上持续采集前台应用，保存本机应用和前台应用时段。业务规则见 [Foreground State](../../Docs/Heartbeat.Docs/content/docs/observers/application-activity/index.mdx)。

程序在主线程运行 AppKit 事件循环，持续读取时才能取得更新后的前台应用。

## 运行

前置条件：macOS 和 `global.json` 指定的 .NET SDK。发送数据需要已应用迁移的 Heartbeat API。API 不可用时，程序继续采集并在本地保存。

在仓库根目录运行：

```sh
dotnet run --project src/Observers/Heartbeat.Observer.ForegroundState
```

按 Ctrl+C 停止。只采集指定次数：

```sh
dotnet run --project src/Observers/Heartbeat.Observer.ForegroundState -- --samples 10
```

## 参数

| 参数 | 默认值 | 含义 |
| --- | --- | --- |
| `--api` | `http://localhost:8080/api/` | API 的 HTTP 或 HTTPS 地址，包含路径前缀，不允许查询参数或片段 |
| `--identity-file` | `~/Library/Application Support/Heartbeat/observers/macos-system/identity.json` | Observer 实例的身份文件 |
| `--applications-file` | `~/Library/Application Support/Heartbeat/local-applications.json` | 本机应用登记文件 |
| `--state-file` | 身份文件路径加 `.sqlite` | 进行中的时段和 Outbox |
| `--interval-ms` | `1000` | 定时确认的间隔，100 到 60000 毫秒 |
| `--samples` | `0` | 采样次数。`0` 表示持续运行。读取失败也计一次 |

## 文件

| 文件 | 规则 |
| --- | --- |
| 身份文件 | 一个文件对应一个 Observer 实例。启动另一个实例时使用新的身份文件 |
| 状态文件 | 绑定一个 Observer 实例，不能交给另一个实例使用 |
| 本机应用登记文件 | 同一设备上的实例共用。另一台设备使用自己的文件，不复制已有文件 |

- 同一个身份文件或状态文件只允许一个进程使用。`.lock` 文件由操作系统锁定，进程退出后释放。留下的空锁文件不影响重启。
- 文件损坏时，程序报错并保留原文件。
- 删除文件后，程序重新分配文件中管理的身份。

## 输出

每次本地确认，标准输出写一行 JSON，包含 `observerId`、`localApplicationId`、`contentId`、`observationId`、`startAt`、`endAt`、`name` 和 `storedLocally: true`。这一行只表示本地已保存，不表示后端已保存。

后台发送每轮间隔 5 秒，单次请求最多等待 30 秒。Outbox 记录确认后删除，标准错误输出对应的 Observation ID。发送失败的原因写入 Outbox 的 `last_error` 和标准错误。

停止时，程序结束当前时段并放入 Outbox，结束时间是最后一次本地确认。没有发送的记录留到下次运行。

| 退出码 | 含义 |
| --- | --- |
| 0 | 指定次数的采样结束，本地状态已保存 |
| 1 | 本地文件、运行环境或后台任务失败 |
| 2 | 参数错误 |
| 130 | 收到停止请求，未发送的记录留在 Outbox |

## 验证

自动测试覆盖本机应用身份、时段边界、采集事件、休眠边界、本地事务恢复，以及 HTTP 保存和读回。测试不调用 macOS 原生接口。Case 见文档站的 Testing 分区。

```sh
dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD --quality
```

原生读取需要在 macOS 上实际运行。使用独立的 API、数据库和临时文件：

```sh
dotnet run --project src/Observers/Heartbeat.Observer.ForegroundState -- \
	--api http://localhost:5100/ \
	--identity-file /tmp/heartbeat-observer-check/identity.json \
	--applications-file /tmp/heartbeat-observer-check/applications.json \
	--samples 10
```

运行期间切换应用，然后检查：

1. 同一应用的连续确认沿用同一个 Observation ID。
2. 切换应用后出现新的 Observation。
3. 等待后台发送后，能从 API 读回已结束的时段。
4. 再次运行，本机应用的 EntityId 不变，Observation ID 是新的。

停止时刚结束的时段可能留在 Outbox，下次运行时继续发送。真实休眠需要另外验证。自动测试只模拟计时差和采集空白。
