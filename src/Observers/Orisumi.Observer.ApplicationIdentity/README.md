# Orisumi.Observer.ApplicationIdentity

Orisumi.Observer.ApplicationIdentity 是应用识别 Observer：它读取后端已保存的本机应用和前台时段，把还没归属的跨设备线索交给 OpenAI 判断，结果写成线索归属，需要时新建应用产品。业务规则见 [Application Identity](../../Docs/Orisumi.Docs/content/docs/observers/application-identity/index.mdx)。

它发布为服务器平台的安装包，由以服务方式运行的 Runtime 启动，经 .NET SDK 直接读写后端，见 [Hosting · Server](../../Docs/Orisumi.Docs/content/docs/hosting/server.mdx)。

## 运行

构建安装包和在本地运行的步骤见 [Runtime README](../Orisumi.Observer.Runtime/README.md)。

| 配置 | 来源 | 含义 | 默认值 |
| --- | --- | --- | --- |
| `OPENAI_API_KEY` | Runtime 的环境变量 | OpenAI 的 API Key | 无 |
| `model` | 程序实例的设置 | OpenAI 模型型号 | `gpt-6.1-terra` |
| `intervalSeconds` | 程序实例的设置 | 一轮结束到下一轮开始的间隔，必须是正数 | `60` |

没有 Key 时，应用识别不调用模型，每轮报告一次诊断。

## 状态

程序实例的 EntityId 由 Runtime 保存。模型回答未知的线索只记在内存中，进程重启后重新交给模型，见 [Application Identity · 模型](../../Docs/Orisumi.Docs/content/docs/observers/application-identity/index.mdx#model)。

## 验证

测试在 `tests/Orisumi.Integration.Tests/` 中，随集成测试运行，见[集成测试 README](../../../tests/Orisumi.Integration.Tests/README.md)。Case 见 [Testing · 应用识别](../../Docs/Orisumi.Docs/content/docs/testing/observers/application-identity.mdx)。`ApplicationIdentityTests` 经 SDK 和真实 API，连接独立的 PostgreSQL，模型用替身；`ApplicationModelTests` 用 HTTP 替身检查 Responses 请求和回答的解析。真实的 OpenAI 调用不在自动测试中，需要时在临时环境中运行并单独保存结果。
