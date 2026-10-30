using System.Text.Json;

namespace Orisumi.Application.Management;

public sealed record ObserverHost(Guid HostId, string Name, string Kind, bool Revoked, DateTimeOffset? LastSeenAt);
public sealed record InstanceConfig(Guid EntityId, string InstanceKey, bool Enabled, JsonElement Settings,
    long Revision, long LastLocalSequence, bool Removed);
public sealed record ObserverInstance(Guid EntityId, Guid HostId, string InstanceKey, string Name,
    bool Enabled, JsonElement Settings, long Revision, bool Removed, JsonElement? Status, DateTimeOffset? ReportedAt);
public sealed record InstanceRegistration(string InstanceKey, string Name, string Scope, bool Enabled,
    JsonElement Settings, long LocalSequence);
public sealed record ConfigurationChange(bool? Enabled, JsonElement? Settings);
public sealed record InstanceRevision(Guid EntityId, long Revision);
public sealed record RuntimeOperation(Guid OperationId, string Action, Guid? EntityId, string? PackageId, bool NewInstance);
public sealed record OperationRequest(string Action, Guid? EntityId, string? PackageId, bool NewInstance);
public sealed record ObserverOperation(Guid OperationId, Guid HostId, string Action, Guid? EntityId,
    string? PackageId, bool NewInstance, string State, string? Error, JsonElement? Result);
public sealed record OperationResult(string Outcome, string? Error, JsonElement? Result);
public sealed record RuntimePoll(IReadOnlyList<InstanceConfig> Instances, IReadOnlyList<RuntimeOperation> Operations);
public sealed record Pairing(string Code, DateTimeOffset ExpiresAt, Guid? HostId);
public sealed record HostCredential(Guid HostId, string Credential);

public sealed class ManagementException(int status, string? code, string message, long? lastLocalSequence = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    public long? LastLocalSequence { get; } = lastLocalSequence;
}
