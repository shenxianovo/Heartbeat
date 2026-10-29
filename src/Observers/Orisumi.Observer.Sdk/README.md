# Orisumi.Observer.Sdk

Runtime 用这个类库把程序实例的版本交给后端：草稿和待发送的版本保存在本地 SQLite 中，按依赖顺序提交，按响应确认。Observer 不直接引用它，而是通过 [Orisumi.Observer.Protocol](../Orisumi.Observer.Protocol/README.md) 连接 Runtime；`LocalObserverSession` 把同样的请求直接交给这个类库，供测试使用。版本的状态、提交策略和确认规则见 [Submission](../../Docs/Orisumi.Docs/content/docs/observers/submission/index.mdx)，本地保存见 [.NET](../../Docs/Orisumi.Docs/content/docs/observers/submission/dotnet.mdx)。

## 用法

每个程序实例创建一个 `ObserverClient`，传入 SQLite 文件、实例的 EntityId 和指向 API 的 `HttpClient`（`BaseAddress` 以 `/api/` 结尾）。`HttpClient` 由调用方创建和释放。

```csharp
using var http = new HttpClient { BaseAddress = new("http://localhost:8080/api/") };
using var sdk = new ObserverClient(statePath, instanceId, http);

// 登记：先 EntitySchema，再程序实例。登记只写入本地，发送前再与后端的版本链对齐
var schema = sdk.RegisterSchemaLocally(schemaId, entitySchema);
sdk.RegisterInstance(instanceId, observerSchema, instanceContent);

// 形成观测：一次 Execute 是一个本地事务
sdk.Execute(local =>
{
    local.Draft(contentId, content, schema, SubmissionStrategy.OnCompletion);
    var observer = local.Reference(instanceId);
    var target = local.Reference(contentId);
    local.Draft(observationId, new Observation(observer, target, null, start, end, timeZone),
        strategy: SubmissionStrategy.OnCompletion, publishDraft: true);
});

// 事实结束时
sdk.Execute(local => local.Complete(observationId));

// 后台发送，失败时按间隔重试
await sdk.RunAsync(TimeSpan.FromSeconds(5), status => Console.Error.WriteLine(status), stoppingToken);
```

- 对同一实体再次调用 `Draft`：最新版本是草稿时合并进去，否则形成新版本。
- `Reference` 返回实体在本地最新的 VersionId，并冻结这个版本。
- `SubmissionStrategy.Immediate` 的草稿在发送时冻结，`OnCompletion` 的草稿在 `Complete` 时冻结。

## 观测草稿

Observation 的 `Draft` 传入 `publishDraft: true`，SDK 就把这份草稿写入后端，读取方用 `GET /api/observation-drafts/{entityId}` 读到进行中的 Observation。约定见 [Submission · 观测草稿](../../Docs/Orisumi.Docs/content/docs/observers/submission/index.mdx#drafts)。

- 草稿引用的版本都已保存后，每次 `SendAsync` 先写入草稿最新的内容，再发送待发送的版本。`sequence` 保存在 SQLite 中，重启后继续递增。
- 写入失败时，下一次发送写入最新的内容。后端返回 `VERSION-6` 时，SDK 停止写入这份草稿。
- 草稿冻结时停止写入，例如 `Complete` 之后。后端保存这个版本后移除草稿。
- `ObserverClient.PublishDrafts` 控制是否写入，默认打开。Runtime 只在 Observer 进程报告 `ready` 之后、退出之前打开它。

## 被拒绝的版本

`sdk.Status.Rejection` 给出被拒绝的版本、`rule` 和依赖它的版本，处理前暂停发送。在 `Execute` 中选择一种处理：

| 方法 | 作用 |
| --- | --- |
| `AdoptSaved` | 采用后端已保存的、内容相同的版本 |
| `ReplaceRejected` | 用新的版本替换被拒绝的版本，待发送版本中的引用随之改写 |
| `ReplaceReferenceAndResume` | 把待发送版本中对某个版本的引用换成另一个版本，再重新发送被拒绝的版本 |
| `DiscardRejected` | 丢弃被拒绝的版本和依赖它的版本 |

前台状态 Observer 的用法见 `src/Observers/Orisumi.Observer.ForegroundState/ForegroundStore.cs` 和 `ForegroundDelivery.cs`。
