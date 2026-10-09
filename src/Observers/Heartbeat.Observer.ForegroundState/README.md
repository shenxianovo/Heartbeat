# Heartbeat.Observer.ForegroundState

本项目运行前台状态 Observer。应用与窗口标题观测共用实例身份，分别使用自己的观测定义和时间线。当前仅实现 macOS 应用单次读取、提交和读回核对。窗口标题、连续采集和 Windows 读取尚未实现。

观测规则见 [前台状态 Observer](../../Docs/Heartbeat.Docs/content/docs/observers/foreground-state.mdx)。主体、平台客户端和实例身份的职责见 [ADR 0002](../../Docs/Heartbeat.Docs/content/docs/adr/0002-observer-capabilities-and-platform-clients.mdx)。

## 运行

前置条件：

- 运行环境为 macOS。
- 已安装仓库 `global.json` 指定的 .NET SDK。
- Heartbeat API 已启动，且数据库已应用项目迁移。

从仓库根目录执行：

```sh
dotnet run --project src/Observers/Heartbeat.Observer.ForegroundState
```

检查结果：程序退出码为 0，且标准输出 JSON 中的 `verified` 为 `true`。

## 参数

默认 API 基地址为 `http://localhost:8080/api/`。默认身份文件为 `~/Library/Application Support/Heartbeat/observers/macos-system/identity.json`。

可以指定 API 基地址和身份文件：

```sh
dotnet run --project src/Observers/Heartbeat.Observer.ForegroundState -- \
  --api http://localhost:8080/api/ \
  --identity-file /path/to/instance/identity.json
```

`--api` 必须为 HTTP 或 HTTPS 绝对地址，且不得包含查询参数或片段。地址必须包含 API 路径前缀，末尾的 `/` 可以省略。直接连接 API 服务时，使用服务地址，例如 `http://localhost:5000/`。

`--identity-file` 指定前台状态 Observer 的实例身份文件。创建独立实例时，指定另一份新建身份文件。

## 身份文件

身份文件只保存前台状态实例的 `observerId`。

读到前台应用后，程序按文件状态选择动作：

- 文件不存在时，生成 UUIDv7 实例 ID 并写入文件。
- 文件包含有效的 JSON 和 UUIDv7 `observerId` 时，读取原 ID。
- JSON 或 `observerId` 无效时，报告错误并保留原文件。

独立主体实例使用各自的身份文件。新增输出类型不直接要求新建身份。

删除身份文件后，下次运行会生成新的实例 ID。

## 输出

程序将进度、实体 ID 和错误写入标准错误。

提交时，程序依次保存 `Observer`、`ObservationSchema`、具体数据实体和 `Observation`。随后，程序通过统一实体读取入口核对四个实体的 `category` 和 `entity`。

如果四个实体均与提交内容一致，程序向标准输出写入一行 JSON：

| 字段 | 内容 |
| --- | --- |
| `observerId` | 前台状态 Observer 实例 ID |
| `schemaId` | 观测定义 ID |
| `dataId` | 本次具体数据实体 ID |
| `observationId` | 本次观测 ID |
| `observedAt` | 本次读取的 UTC 时间，精度为微秒 |
| `timeZone` | 本机 IANA 时区 ID；未知时为 `null` |
| `application` | 应用读数，字段见[当前单次读数](../../Docs/Heartbeat.Docs/content/docs/observers/foreground-state.mdx#当前单次读数) |
| `verified` | 固定为 `true` |

退出码：

| 退出码 | 含义 |
| --- | --- |
| 0 | 提交和读回核对成功 |
| 1 | 采集、提交或读回核对失败，或操作取消 |
| 2 | 参数错误 |

## 失败处理

每个 HTTP 请求的超时时间为 30 秒。如果请求超时、提交失败或读回核对失败，程序报告失败并退出。后端保留已成功保存的实体。

程序不自动重试，也不使用离线队列。重新运行程序会形成新的读数。

## 验证

```sh
dotnet run --project tools/Heartbeat.Dev -- verify closeout --base HEAD
```

集成测试覆盖身份复用、独立实例、并发首次创建和损坏文件保留。提交用例通过真实 API 和独立 PostgreSQL，检查读回内容、重复提交和历史快照。观测写入失败用例检查后端保留已保存的实体。

自动测试不调用 macOS 原生接口。验证原生读取前，准备独立 API 和测试数据库。

1. 在 macOS 上运行本程序，使用 `--api` 指向测试 API，并使用 `--identity-file` 指定临时身份文件。
2. 验证：退出码为 0，且标准输出的 `verified` 为 `true`。
