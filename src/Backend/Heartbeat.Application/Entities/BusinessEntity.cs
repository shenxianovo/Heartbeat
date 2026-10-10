using System.Text.Json;

namespace Heartbeat.Application.Entities;

public sealed record BusinessEntity(JsonElement References, JsonElement Properties);
