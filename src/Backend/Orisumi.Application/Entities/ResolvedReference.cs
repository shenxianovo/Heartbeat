using Orisumi.Core;

namespace Orisumi.Application.Entities;

public sealed record ResolvedReference(string Position, VersionId? TargetVersionId, EntityId TargetEntityId);
