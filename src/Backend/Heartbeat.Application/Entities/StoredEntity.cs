using Heartbeat.Core;

namespace Heartbeat.Application.Entities;

public sealed record StoredEntity(string Category, object Value, EntityId? SchemaId = null);
