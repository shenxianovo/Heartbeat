using System.ComponentModel;

namespace Heartbeat.Api.Entities;

[Description("观测主体除 id 外的全部属性。")]
public sealed class SaveObserverRequest
{
    [Description("观测主体的名称。")]
    public required string Name { get; init; }
}
