# Collector 接入 Hub

本模块提供提交声明、HTTP 回执核对，以及 Collector 进程内的 `PendingHubSubmissions`。责任见 [ADR-0027](../../../docs/adr/ADR-0027-shared-collector-handoff-buffer.md)，持久接管契约见 [Hub 记录交付](../../../docs/hub-record-delivery.md)。

## 先运行一个 Collector

仓库提供[完整示例](../../../tools/Heartbeat.Collector.Sample/SampleCollector.cs)和[进程入口](../../../tools/Heartbeat.Collector.Sample/Program.cs)。它引用本模块，通过 HTTP 向 Hub 交接两种记录：一次手动确认（Point），以及按演示规则确认的一段进程运行时间（Range）。记录使用 `example.*` 类型和对象命名空间，不代表真实用户活动。

在仓库根目录准备[本地环境](../../../docs/development.md#环境准备)，已有配置可跳过 setup：

```bash
dotnet run --project tools/Heartbeat.Dev -- env setup
dotnet run --project tools/Heartbeat.Dev -- env up web hub
```

在另一个终端设置环境变量并启动示例；PowerShell 使用 `$env:变量名 = '值'` 设置相同变量。

```bash
export HEARTBEAT_HUB_URL='http://127.0.0.1:4318'
export HEARTBEAT_HUB_TOKEN='<.env.local 中的 HEARTBEAT_HUB_TOKEN>'
export HEARTBEAT_SAMPLE_TARGET='sample-local'
dotnet run --project tools/Heartbeat.Collector.Sample
```

Hub 密钥是本地交接密钥，与后端 API key 不同。示例不自动读取 `.env.local`；勿提交真实密钥。Target 标识这一个演示来源，重启时保持相同，不应为每次启动生成新值；换一个来源时换 Target。

| 输入 | 可观察的结果 |
| --- | --- |
| 回车或 `observe` | 新建一个 Point，并打印它和当前 Range 的 ID、时间 |
| 5 秒内再次 `observe` | Point 获得新 ID，Range 保持 ID，只延长结束时间 |
| `gap` 后再 `observe`，或两次观测间隔超过 5 秒 | 开始新 Range，旧 Range 停在最后一次确认处 |
| `retry` | 重交未确认的快照，保持原 ID 和内容，不制造新观测 |
| `quit`、输入结束或 Ctrl+C | 用独立的 5 秒期限做最后一次交接，结束时间保持最后一次观测值 |

每次有效命令都会尝试交接；离线时快照留在内存，不自动后台重试。可以暂时停止 Hub，在示例中输入 `observe`，恢复 Hub 后输入 `retry`，核对 ID 保持相同。退出码 0 表示本次快照均已获 Hub 接管确认；1 表示退出时仍有未确认快照并将丢失；2 表示环境配置无效。Hub 接管不等于后端落库，后续交付由 Hub 负责。

后端交付完成后，以相同 Owner 登录 Web，在 `/objects` 找到“演示来源”，查看 Point 和 Range。示例类型没有专用 renderer，详情采用通用 JSON 展示。这个独立进程不注册 Hub 管理页中的启停控制。

## 换成自己的观测来源

保留稳定的 Collector key 和 Target，为每类记录声明 Track。选择 Point 或显式结束时间的 Range；每个新事实生成一次 ID，续期和重传沿用它。Range 的连续性规则应来自真实来源的观测能力，示例的 5 秒只是演示约定。`value` 或对象引用改变时开始新 Record；每条提交显式携带 `objects`，无对象时使用空数组。对象身份与展示规则见[记录对象](../../../docs/record-objects.md)。

新增类型通常只需实现采集与声明。Hub 负责后端注册和映射，通用存储与 JSON 展示可直接接收；需要专用展示时再增加 renderer。

## 最小交接流程

Collector 持有缓冲和 `IHubSubmissionClient`；同进程使用 Hub 提供的 `LocalHubSubmissionClient`，跨进程使用 `HubSubmissionClient`。最小交接流程：

```csharp
using System.Text.Json;
using Heartbeat.Hub;
using Heartbeat.Contracts;

var pending = new PendingHubSubmissions();
var route = new SubmissionRoute(
    new CollectorDeclaration("example.collector", "device-a", "Example"),
    new TrackDeclaration("example.range", 1, "range"));
var startedAt = DateTimeOffset.UtcNow;
var record = new RecordSnapshot(Guid.CreateVersion7(startedAt), startedAt, startedAt,
    null, JsonSerializer.SerializeToElement(new { status = "observed" }))
{
    Objects = [new("source", "example.manual", "device-a", "演示来源")],
};
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
