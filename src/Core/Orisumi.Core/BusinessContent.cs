using System.Text.Json;

namespace Orisumi.Core;

public sealed record BusinessContent : VersionContent
{
    public BusinessContent(JsonElement fields) => Fields = fields.Clone();
    public JsonElement Fields { get; }
    public override string Category => "business";
}
