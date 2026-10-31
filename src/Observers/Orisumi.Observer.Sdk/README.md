# Orisumi.Observer.Sdk

.NET 编写的 Observer 用这个类库写入和读取。写入接口是全部契约，这个类库只省掉请求编码、文件上传和运行上下文的读取，方法的语义见 [API · SDK](../../Docs/Orisumi.Docs/content/docs/api/sdk.mdx)。

## 用法

```csharp
await using var data = DataClient.Connect();
await data.UseAsync(definitions, observerSchemaId, new { programVersion = data.PackageVersion });

await data.WriteAsync(new(temperatureSchemaId, new { celsius = 21.5 }, Time: FactTime.Point(now, zone)));
```

- `Connect` 在受托管时从运行上下文读取地址，见 [Hosting · 运行上下文](../../Docs/Orisumi.Docs/content/docs/hosting/index.mdx#context)。直接运行时传入后端地址和用识别依据写的 Observer。
- `data.Stopping` 在标准输入关闭时取消。业务代码据此结束并写入进行中的事实，然后退出。
- `data.Direct` 连后端地址，同步得到写入结果；被拒绝时抛出 `WriteRejectedException`。写给本地转发时，收下就返回。
- 内容中的 `binary` 字段写 `BinaryContent`，SDK 先上传字节，再发送写入。
- 用到设备时，声明 `DeviceContext.Definition`，写入 `data.Device.Write`，用 `data.Device.Ref` 引用它。
- 业务类型的声明保存为嵌入资源的 JSON，用 `SchemaDefinition.Load` 读取。
- 日志和采集诊断写入标准错误。
