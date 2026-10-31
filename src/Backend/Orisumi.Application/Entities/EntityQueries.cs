using Orisumi.Core;

namespace Orisumi.Application.Entities;

public sealed class EntityDeletedException() : Exception("The requested identity has been deleted.");
public sealed class ResourceNotFoundException() : Exception("No EntitySchema uses this resourceName.");

public sealed record EntityQuery(string? Category = null, EntityId? SchemaId = null, int Limit = 100, EntityId? After = null);
public sealed record ObservationCursor(DateTimeOffset? Time, EntityId EntityId);
public sealed record ObservationQuery(DateTimeOffset? From = null, DateTimeOffset? To = null, EntityId? SchemaId = null,
    int Limit = 100, ObservationCursor? After = null, EntityId? ObserverEntityId = null, EntityId? ContentEntityId = null, bool Descending = false);
