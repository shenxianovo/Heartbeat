# Collector 接入 Hub

本模块提供提交声明、HTTP 回执核对，以及 Collector 进程内的 `PendingHubSubmissions`。责任见 [ADR-0027](../../../docs/adr/ADR-0027-shared-collector-handoff-buffer.md)，持久接管契约见 [Hub 记录交付](../../../docs/hub-record-delivery.md)。

Collector 持有缓冲和 `IHubSubmissionClient`；同进程使用 Hub 提供的 `LocalHubSubmissionClient`，跨进程使用 `HubSubmissionClient`。最小交接流程：

```csharp
using System.Text.Json;
using Heartbeat.Hub;

var pending = new PendingHubSubmissions();
var route = new SubmissionRoute(
    new CollectorDeclaration("example.collector", "device-a", "Example"),
    new TrackDeclaration("example.range", 1, "range", "explicit"));
var startedAt = DateTimeOffset.UtcNow;
var record = new RecordSnapshot(Guid.CreateVersion7(startedAt), startedAt, startedAt,
    null, JsonSerializer.SerializeToElement(new { status = "observed" }));
pending.Stage(route, record);

// hub 由宿主提供；仅在实际再次观测确认后，以相同 ID 暂存增长的 endedAt。
foreach (var batch in pending.ReadBatches())
{
    await hub.SubmitAsync(batch.ToSubmission(), cancellationToken);
    pending.Confirm(batch);
}
```

`ReadBatches` 按完整声明分组，以 `HubSubmissionLimits` 的 500 条和 1 MiB 上限分批，字节预算包含 JSON 声明、转义和分隔符。单条无法放入提交时抛出异常，不丢弃快照。一个待交接 Record 的 Collector key、Target、Track type/version 和时间定义不能改变；Collector 展示名可以更新，不改变记录归属。同 ID 的固定字段仍须符合 Hub 不变量。

提交抛出异常时不要调用 `Confirm`，保留缓冲实例并按 Collector 的策略重试。确认只移除声明和 Record 都与发送快照仍一致的项；发送期间 `Stage` 的新续期或展示名更新继续等待下一轮。缓冲允许并发暂存与确认，但不会自动发送或重试。调用方必须保持 Record 的 JSON 内容在交接期间有效。

这只是内存缓冲。进程崩溃可能丢失 Hub 接管前的数据；成功接管后的恢复由 Hub SQLite 负责。桌面和 VRChat 保留各自原有的连续性、重试和停止策略。
