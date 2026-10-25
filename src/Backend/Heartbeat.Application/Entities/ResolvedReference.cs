using Heartbeat.Core;

namespace Heartbeat.Application.Entities;

public sealed record ResolvedReference(string Position, VersionId TargetVersionId, EntityId TargetEntityId);
