using Heartbeat.Core;

namespace Heartbeat.Observers.ForegroundState;

internal sealed record LocalApplication(EntityId Id, IReadOnlyList<ApplicationIdentifier> Identifiers);
