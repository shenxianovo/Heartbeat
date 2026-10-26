using Orisumi.Core;

namespace Orisumi.Application.Entities;

public sealed record Referrer(EntityVersion Version, IReadOnlyList<VersionReference> References);
public sealed record ReferrerQuery(EntityId Target, bool AllVersions = false, int Limit = 100, VersionId? After = null);
