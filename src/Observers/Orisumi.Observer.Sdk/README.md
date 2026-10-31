# Orisumi.Observer.Sdk

.NET 编写的 Observer 用这个类库连接 [Runtime](../../Docs/Orisumi.Docs/content/docs/hosting/runtime.mdx)。它只处理标准输入和标准输出中的逐行 JSON：读取 `init` 和 `stop`，发出请求，把响应按 `id` 对应回去。形成版本和提交在 Runtime 中，见 [.NET](../../Docs/Orisumi.Docs/content/docs/hosting/dotnet.mdx)。

## 用法

```csharp
await using var session = new PipeObserverSession(Console.In, Console.Out);
var init = await session.Initialization;
var instanceId = init.GetProperty("instanceId").GetGuid();

// 登记之后
await session.RequestAsync("ready", JsonLines.Json(new { }), cancellationToken: session.Stopping);

// 一次业务变化是一个批次，返回时已保存到后端
var result = await session.RequestAsync("commit", JsonLines.Json(new { operations }));
```

- `session.Stopping` 在收到 `stop` 或管道关闭时取消。业务代码据此结束并提交进行中的事实，然后退出。
- 写入的内容中有 `binary` 字段时，用 `BinaryContent` 从字节和媒体类型生成描述：`Descriptor` 写进内容，`Payload` 放进同一批次的 `contents`，见 [Submission · 批次](../../Docs/Orisumi.Docs/content/docs/hosting/submission.mdx#batch)。

```csharp
var icon = new BinaryContent(png, "image/png");
await session.RequestAsync("commit", JsonLines.Json(new { operations, contents = new[] { icon.Payload } }));
```

- 后端拒绝提交时，`RequestAsync` 抛出 `SubmissionRejectedException`，带着 `Rule` 和被拒绝的 `VersionId`；后端不可用时抛出 `HttpRequestException`。
- 请求和结果的字段见 [Protocol · Observer 管道](../../Docs/Orisumi.Docs/content/docs/hosting/protocol.mdx#observer-pipe)。
- 日志和采集诊断写入标准错误，标准输出只写协议消息。
