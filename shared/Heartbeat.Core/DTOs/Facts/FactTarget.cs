namespace Heartbeat.Core.DTOs.Facts;

/// <summary>An offline business reference. Devices use hardware identity; application contexts use a device/platform pair; accounts use a service/account pair.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FactTarget(string Kind, string Reference);
