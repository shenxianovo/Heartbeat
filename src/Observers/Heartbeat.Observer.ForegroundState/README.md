# Heartbeat.Observer.ForegroundState

在 macOS 上持续采集前台应用，保存本机应用身份和应用时段。主线程运行 AppKit 事件循环，使持续读取能够取得更新后的应用状态。业务规则见[前台状态](../../Docs/Heartbeat.Docs/content/docs/observers/foreground-state.mdx)。

## 运行

前置条件：macOS、仓库指定的 .NET SDK。发送时需要已应用迁移的 Heartbeat API；离线时仍可采集。

从仓库根目录运行：

```sh
dotnet run --project src/Observers/Heartbeat.Observer.ForegroundState
```

按 Ctrl+C 停止。程序不延长最后一次已保存的确认时间。

限定采样次数：

```sh
dotnet run --project src/Observers/Heartbeat.Observer.ForegroundState -- --samples 10
```

## 参数

| 参数 | 默认值 | 含义 |
| --- | --- | --- |
| `--api` | `http://localhost:8080/api/` | 包含路径前缀的 HTTP(S) 地址，不允许查询参数或片段 |
| `--identity-file` | `~/Library/Application Support/Heartbeat/observers/macos-system/identity.json` | Observer 实例身份文件 |
| `--applications-file` | `~/Library/Application Support/Heartbeat/local-applications.json` | 本机应用身份登记文件，同一设备上的实例共用 |
| `--state-file` | 身份文件路径加 `.sqlite` | 当前时段与 Outbox |
| `--interval-ms` | `1000` | 兜底确认间隔，允许 100–60000 毫秒 |
| `--samples` | `0` | 采样尝试次数；0 表示持续运行，读取失败也计一次 |

另一 Observer 实例使用新的身份文件，可以继续共享本机应用登记文件。同一身份文件或状态文件只允许一个采集进程运行。状态文件绑定 Observer 身份，不能交给另一个实例使用。`.lock` 文件由操作系统锁定；进程退出后自动释放，保留空锁文件不影响重启。

另一台设备使用自己的身份文件与应用登记文件，不复制已有文件。文件损坏时报错并保留原文件。删除文件会重新分配其中管理的身份。

## 保存与输出

程序通过 `local-applications` 保存应用标识线索，通过 `foreground-application-periods` 保存应用引用和名称，再保存 Observation 的起止时间。定义与数据字段见[本机应用](../../Docs/Heartbeat.Docs/content/docs/observers/entities/local-application.mdx)和[应用时段](../../Docs/Heartbeat.Docs/content/docs/observers/schemas/foreground-application-interval.mdx)。

应用激活事件触发采集，每秒定时确认作为兜底。当前时段保存在 SQLite，结束后才进入 Outbox。停止或重启时，按最后一次本地确认结束时段。

后台查询已结束时段的六个实体，缺少时提交，后续轮次全部读回一致后删除本地记录。发送完成不删除记录。每轮结束后等待 5 秒，单次请求最多等待 30 秒。断网与 HTTP 错误不阻塞采集，原因写入 Outbox 的 `last_error` 和标准错误。状态数据库随实例保留，重启后继续处理。

标准输出每次本地确认写入一行 JSON，包含 `observerId`、`localApplicationId`、`contentId`、`observationId`、`startAt`、`endAt`、`name` 和 `storedLocally: true`。它表示本地已保存，不表示服务器已保存。后台确认并移除记录时，标准错误输出对应 Observation ID。

限定次数结束或 Ctrl+C 停止时，当前时段进入 Outbox；未发送的记录留到下次运行。需要服务器保存的时段必须先结束，因此后端不显示正在进行的时段。

| 退出码 | 含义 |
| --- | --- |
| 0 | 指定次数的确认尝试结束，本地状态已保存 |
| 1 | 本地文件、运行环境或采集后台任务失败 |
| 2 | 参数错误 |
| 130 | 收到停止请求，未确认的记录留在 Outbox |

## 验证

```sh
dotnet run --project tools/Heartbeat.Dev -- verify closeout --base HEAD
```

自动测试覆盖应用身份、时段边界、采集事件、休眠边界、本地事务恢复、HTTP 保存与读回。Case 文档位于文档站的 Testing → 前台应用采集。

原生读取使用独立 API、数据库和临时文件验证：

```sh
dotnet run --project src/Observers/Heartbeat.Observer.ForegroundState -- \
  --api http://localhost:5100/ \
  --identity-file /tmp/heartbeat-observer-check/identity.json \
  --applications-file /tmp/heartbeat-observer-check/applications.json \
  --samples 10
```

运行期间切换应用。检查：同一应用连续读数沿用观测 ID，切换后出现新观测，切换后等待后台核对，能够读回已结束时段。停止时新结束的时段可能留在 Outbox，下次运行后继续发送。重新运行并检查本机应用身份保持不变，观测 ID 更新。真实休眠验证需另行执行；自动测试只模拟计时差和采集空白。
