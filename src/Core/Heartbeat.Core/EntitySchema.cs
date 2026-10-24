using System.Text.Json;

namespace Heartbeat.Core;

public sealed record EntitySchema : VersionContent
{
    public EntitySchema(string displayName, string description, string resourceName, JsonElement fields)
    {
        DisplayName = displayName;
        Description = description;
        ResourceName = resourceName;
        Fields = fields.Clone();
    }

    public string DisplayName { get; }
    public string Description { get; }
    public string ResourceName { get; }
    public JsonElement Fields { get; }
    public override string Category => "entity_schema";
}
