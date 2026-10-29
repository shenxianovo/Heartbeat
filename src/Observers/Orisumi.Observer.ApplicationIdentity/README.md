# Orisumi.Observer.ApplicationIdentity

Orisumi.Observer.ApplicationIdentity 是应用识别 Observer：它读取后端已保存的本机应用和前台时段，把还没归入产品的跨设备线索交给 OpenAI 判断，结果写成应用产品的版本。业务规则见 [Application Identity](../../Docs/Orisumi.Docs/content/docs/observers/application-identity/index.mdx)。

它是一个类库，由 API 进程中的[服务端托管](../../Docs/Orisumi.Docs/content/docs/observers/submission/index.mdx#server)运行。每一轮直接调用实体服务读取和提交，调用 OpenAI 的 Responses API，不经过 HTTP 和 Runtime。

## 运行

API 启动后，服务端托管随之开始应用识别的轮次，没有单独的进程。本地环境中启动 `api` 服务即可，配置的传入方式见 [AppHost README · 配置](../../../tools/Orisumi.AppHost/README.md#配置)。诊断写入 API 的日志，用 `orisumi env logs api` 读取。

| 配置 | 环境变量 | 含义 | 默认值 |
| --- | --- | --- | --- |
| `OPENAI_API_KEY` | `OPENAI_API_KEY` | OpenAI 的 API Key | 无 |
| `ApplicationIdentity:Model` | `ApplicationIdentity__Model` | OpenAI 模型型号 | `gpt-6.1-terra` |
| `ApplicationIdentity:IntervalSeconds` | `ApplicationIdentity__IntervalSeconds` | 一轮结束到下一轮开始的间隔，必须是正数 | `60` |

没有 Key 时 API 照常运行，应用识别不调用模型，每轮在日志中报告一次诊断。服务端托管仍会登记程序实例，并先恢复已有的未决提交。

## 状态

程序实例的 EntityId、正在提交的批次和模型回答未知的线索，都由服务端托管保存在 PostgreSQL 中，见 [Storage · 服务端托管](../../Docs/Orisumi.Docs/content/docs/storage/index.mdx#server-observers)。API 重启后，服务端托管先用相同的 VersionId 重发未完成的批次，再开始新的一轮。

一个部署中只有一个 API 副本运行服务端托管，见 [ADR · 服务端托管](../../Docs/Orisumi.Docs/content/docs/adr/server-observer-hosting.mdx)。

## 验证

测试在 `tests/Orisumi.Integration.Tests/` 中，随集成测试运行，见[集成测试 README](../../../tests/Orisumi.Integration.Tests/README.md)。`ApplicationIdentityTests` 用模型替身和独立的 PostgreSQL 检查识别、恢复和轮次调度；`ApplicationModelTests` 用 HTTP 替身检查 Responses 请求和回答的解析。真实的 OpenAI 调用不在自动测试中，需要时在临时环境中运行并单独保存结果。
