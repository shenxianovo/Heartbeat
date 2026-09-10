namespace Heartbeat.Core.DTOs.Facts;

/// <summary>An offline business reference. Device references use the stable hardware identity.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FactTarget(string Kind, string Reference);
