using Orisumi.Core;

namespace Orisumi.Observers.ForegroundState;

internal sealed record LocalApplication(EntityId Id, IReadOnlyList<ApplicationIdentifier> Identifiers);
