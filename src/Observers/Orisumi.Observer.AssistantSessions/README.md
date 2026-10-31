# Orisumi.Observer.AssistantSessions

Orisumi.Observer.AssistantSessions 读取当前用户的 Claude Code 和 Codex 会话日志，把会话和其中的每次交互交给 [Runtime](../../Docs/Orisumi.Docs/content/docs/hosting/runtime.mdx) 保存和发送。业务规则见 [Assistant Sessions](../../Docs/Orisumi.Docs/content/docs/observers/assistant-sessions/index.mdx)。

首次运行导入现存的全部会话，之后每 5 秒轮询一次。它只读取 `~/.claude` 和 `~/.codex` 下的文件，不启动这两个产品，也不改动它们的配置。

| 文件 | 内容 |
| --- | --- |
| `Program.cs` | 连接 Runtime、登记 EntitySchema、每 5 秒轮询 |
| `SourceFiles.cs` | 发现日志文件、文件身份、按段读取完整的行、解压归档 |
| `ClaudeSource.cs`、`CodexSource.cs` | 两种来源的记录到交互的对应 |
| `AssistantCollector.cs` | 每一轮的读取顺序和时间预算 |
| `AssistantBatch.cs`、`AssistantDelivery.cs` | 组成批次、交给 Runtime、处理被拒绝的版本 |
| `AssistantState.cs` | 数据目录中的 `capture.sqlite`：身份登记、读取进度、待配对的推理和待确认的批次 |

它引用 `Orisumi.Observer.Sdk` 和 Core，通过标准输入和标准输出与 Runtime 通信。Codex 归档的 zstd 由随包发布的 ZstdSharp.Port 解压，不需要安装 zstd 命令。

## 运行

程序由 Runtime 启动，平时随 [macOS 客户端](../../Clients/Orisumi.Mac/README.md)附带，安装包 ID 是 `assistant-sessions`。前置条件：`global.json` 指定的 .NET SDK；上传需要运行中的 API，例如用 `orisumi env up api` 启动的本地环境。

单独构建安装包：

```sh
orisumi observers pack assistant-sessions --target osx-arm64 --output <目录> --sign -
```

不启动客户端时，用 Runtime 的 `--run` 运行它：

```sh
dotnet run --project src/Observers/Orisumi.Observer.Runtime -- \
	--data-dir /tmp/orisumi-assistant-check \
	--backend http://localhost:8080/api/ \
	--bundled <目录>/bundled \
	--run assistant-sessions \
	--pairing-code <配对码>
```

配对码在 Web 控制台的托管页用“接入”签发。带配对码时，Runtime 接入后端并上传；不带配对码时，版本只在本地保存，状态中写着 `Runtime is unpaired`，适合只看本地采集。按 Ctrl+C 停止，结束后删除数据目录。

首次运行会导入 `$HOME` 下的全部会话。只想导入几个会话时，把它们复制到一个临时目录的 `.claude/projects/<任意名>/` 或 `.codex/sessions/` 下，再用 `HOME=<临时目录>` 运行。

## 启动

程序从 Runtime 的 `init` 读取 `instanceId`、`packageVersion` 和 `dataDirectory`，没有设置项。

- 启动时，程序先重发上次留下的待确认批次，再回复 `ready`。
- 采集诊断写到标准错误，Runtime 把最近的一条作为采集提示报告给客户端。
- 收到 `stop` 后，程序停止轮询，以 0 退出。初始化或运行失败时以 1 退出。

## 验证

自动测试使用临时目录中合成的日志，覆盖记录的对应、工具与子 Agent 的关系、读取与恢复、时间与位置，以及原始记录经真实 API 读回。Case 见文档站 Testing 分区的 Assistant 各页。

```sh
orisumi verify checks --base HEAD --quality
```

真实日志需要在本机实际运行：按[运行](#运行)构建并启动，然后检查：

1. 在 Claude Code 或 Codex 中发一句话，10 秒内 Web 控制台的会话页出现这句话和助手的回复。
2. 重启程序，已有的交互没有重复。
3. 标准错误中的诊断只来自确实不认识的记录。

Windows 和 Linux 没有运行验证。
