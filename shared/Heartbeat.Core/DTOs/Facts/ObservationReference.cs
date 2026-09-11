namespace Heartbeat.Core.DTOs.Facts;

/// <summary>An offline object identity. Analytics resolves aliases to one canonical Object.</summary>
public sealed record ObservationObjectReference(string Kind, string Scope, string Key);

/// <summary>A relation evidenced by this Fact; its valid range is the Fact's own time.</summary>
public sealed record FactRelationSnapshot(string Kind, List<FactRelationMember> Members);

public sealed record FactRelationMember(string Role, ObservationObjectReference Object);

public static class ObservationObjectScopes
{
    public const string Machine = "heartbeat.device";
    public const string App = "heartbeat.app";
    public const string AppIdentity = "heartbeat.app-identity";
    public const string Person = "heartbeat.person";
}
