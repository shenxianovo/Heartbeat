using System.Text.Json;

namespace Heartbeat.Management;

// Management describes installed capabilities; it never describes Record protocols.
public sealed record CollectorField(string Name, string Label, string Kind, bool Required = false);
public sealed record CollectorType(string Key, string DisplayName, IReadOnlyList<CollectorField> Fields);
public sealed record CollectorState(string Key, string Target, string DisplayName, string State, string? Error);
public sealed record DeliveryState(long Pending, long Failed, string? Error);
public sealed record HubReport(string DisplayName, string Kind, IReadOnlyList<CollectorType> Types,
    IReadOnlyList<CollectorState> Collectors, DeliveryState Delivery);
public sealed record HubSummary(Guid Id, DateTimeOffset LastSeenAt, bool Online, bool Retired, HubReport Report);
public sealed record CollectorLoginRequest(string Key, JsonElement Input, string? Target = null, Guid? SessionId = null);
public sealed record CollectorLoginResult(string? Target, IReadOnlyList<CollectorField> Fields, string? Error = null, Guid? SessionId = null);
public sealed record HubCommand(Guid Id, DateTimeOffset ExpiresAt, CollectorLoginRequest Login);
public sealed record HubCommandResult(Guid Id, bool Succeeded, string? Error = null, CollectorLoginResult? Login = null);
public sealed record HubCheckIn(Guid SessionId, HubReport Report, HubCommandResult? Result = null);
public sealed record HubCheckInResponse(HubCommand? Command);

public static class HubManagement
{
    public static readonly TimeSpan ActivityInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ActivityTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan CheckInInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
    public const int MaximumBodyBytes = 262_144;
}
