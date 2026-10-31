using System.Text.Json;

namespace Orisumi.Core;

public sealed record EntitySchema : VersionContent
{
    public EntitySchema(string displayName, string description, string resourceName, JsonElement fields, string kind, bool immutable)
    {
        DisplayName = displayName;
        Description = description;
        ResourceName = resourceName;
        Fields = fields.Clone();
        Kind = kind;
        Immutable = immutable;
    }

    public string Kind { get; }
    public bool Immutable { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string ResourceName { get; }
    public JsonElement Fields { get; }
    public override string Category => "entity_schema";
}
