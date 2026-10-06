using System.ComponentModel;
using Heartbeat.Core;

namespace Heartbeat.Api.Entities;

[Description("观测除 id 外的全部属性。引用可以指向尚未入库的实体。已知开始时间必须早于或等于结束时间，比较使用所表示的时刻。")]
public sealed class SaveObservationRequest
{
    [Description("形成本次观测的 Observer 的 UUIDv7。")]
    public required EntityId ObserverId { get; init; }
    [Description("本次观测引用的具体数据实体的 UUIDv7。")]
    public required EntityId DataId { get; init; }
    [Description("解释本次观测的 ObservationSchema 的 UUIDv7。")]
    public required EntityId SchemaId { get; init; }
    [Description("事实开始时间。已知值必须包含 Z 或明确 UTC 偏移；未知时必须为 null。")]
    public required DateTimeOffset? StartAt { get; init; }
    [Description("事实结束时间。未知时必须为 null。边界相等表示时间点，开始早于结束表示左闭右开区间。")]
    public required DateTimeOffset? EndAt { get; init; }
    [Description("事实所属的 IANA 时区 ID，例如 Asia/Shanghai；未知时必须为 null。前端按浏览器或系统时区显示。")]
    public required string? TimeZone { get; init; }
}
