using System.Text.Json.Serialization;
using Heartbeat.Core;

namespace Heartbeat.Api.Entities;

public sealed record EntityResponse(string Category, object References, object Properties,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EntityId? SchemaId = null);
