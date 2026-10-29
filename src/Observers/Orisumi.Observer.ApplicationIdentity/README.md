# Orisumi.Observer.ApplicationIdentity

Orisumi.Observer.ApplicationIdentity 是应用识别 Observer：它读取后端已保存的本机应用和前台时段，把还没归入产品的跨设备线索交给 OpenAI 判断，结果写成应用产品的版本。业务规则见 [Application Identity](../../Docs/Orisumi.Docs/content/docs/observers/application-identity/index.mdx)。

它直接调用 HTTP API 读取和提交，调用 OpenAI 的 Responses API，不经过 Runtime。

## 运行

本地环境默认包含它，见 [AppHost README · 资源](../../../tools/Orisumi.AppHost/README.md#资源)。单独运行时：

```sh
OPENAI_API_KEY=<key> dotnet run --project src/Observers/Orisumi.Observer.ApplicationIdentity -- \
	--backend http://localhost:8080/api/ \
	--data-dir /tmp/orisumi-application-identity
```

| 参数 | 含义 | 默认值 |
| --- | --- | --- |
| `--backend` | API 的根地址，包括 `/api/` | 必填 |
| `--data-dir` | 数据目录 | 必填 |
| `--model` | OpenAI 模型型号 | `gpt-6.1-terra` |
| `--interval-seconds` | 两轮之间的间隔 | `60` |

Key 从环境变量 `OPENAI_API_KEY` 读取。没有 Key 时程序照常运行，每轮在标准错误报告一次，不写入数据。按 Ctrl+C 停止。

## 数据目录

| 文件 | 内容 |
| --- | --- |
| `observer-id` | 程序实例的 EntityId |
| `unknown.json` | 模型回答未知的线索，以及当时的本机应用和名称 |
| `pending.json` | 正在提交的批次。响应丢失或进程重启后，先用相同的 VersionId 重发 |
| `instance.lock` | 同一个数据目录只能由一个进程使用 |

一个部署只运行一个应用识别 Observer。不同数据目录之间不互斥，部署时由配置保证只有一个。

## 验证

测试在 `tests/Orisumi.Integration.Tests/` 中，随集成测试运行，见[集成测试 README](../../../tests/Orisumi.Integration.Tests/README.md)。`ApplicationIdentityTests` 用模型替身、真实 API 和独立的 PostgreSQL 检查识别流程；`ApplicationModelTests` 用 HTTP 替身检查 Responses 请求和回答的解析。真实的 OpenAI 调用不在自动测试中，需要时在临时环境中运行并单独保存结果。
