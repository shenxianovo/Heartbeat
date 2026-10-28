# Orisumi.Observer.Protocol

.NET 编写的 Observer 用这个类库连接 [Runtime](../../Docs/Orisumi.Docs/content/docs/clients/runtime.mdx)。它只处理标准输入和标准输出中的逐行 JSON：读取 `init` 和 `stop`，发出请求，把响应按 `id` 对应回去。本地保存和发送在 Runtime 中，见 [.NET](../../Docs/Orisumi.Docs/content/docs/observers/submission/dotnet.mdx)。

## 用法

```csharp
await using var session = new PipeObserverSession(Console.In, Console.Out);
var init = await session.Initialization;
var instanceId = init.GetProperty("instanceId").GetGuid();

// 登记、恢复之后
await session.RequestAsync("ready", JsonLines.Json(new { }), cancellationToken: session.Stopping);

// 一次业务变化是一个批次；重发时沿用同一个 requestId
await session.RequestAsync("commit", JsonLines.Json(new { operations }), requestId);
```

- `session.Stopping` 在收到 `stop` 或管道关闭时取消。业务代码据此结束进行中的事实，等最后一个批次的回执后退出。
- 请求和结果的字段见 [Runtime · Observer 管道](../../Docs/Orisumi.Docs/content/docs/clients/runtime.mdx#observer-pipe)。
- 日志和采集诊断写入标准错误，标准输出只写协议消息。
