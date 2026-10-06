using System.ComponentModel;
using System.Text.Json;

namespace Heartbeat.Api.Entities;

[Description("观测定义除 id 外的全部属性。已知开始时间必须早于或等于结束时间。提交方判断是否需要新 ID，后端不自动判断语义变化。")]
public sealed class SaveObservationSchemaRequest
{
    [Description("观测定义的名称。")]
    public required string Name { get; init; }
    [Description("具体数据的结构、约束和观测含义，使用任意可存储的 JSON 值；按 jsonb 语义保存和返回。")]
    public required JsonElement Schema { get; init; }
    [Description("有效期开始时间。已知值必须包含 Z 或明确 UTC 偏移；未知时必须为 null。")]
    public required DateTimeOffset? StartAt { get; init; }
    [Description("有效期结束时间。未知时必须为 null。")]
    public required DateTimeOffset? EndAt { get; init; }
}
