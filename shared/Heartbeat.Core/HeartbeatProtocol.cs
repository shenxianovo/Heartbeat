namespace Heartbeat.Core;

public static class HeartbeatProtocol
{
    public const string VersionHeader = "X-Heartbeat-Protocol-Version";
    public const string RequiredVersion = "3";
    public const string UpdateRequiredCode = "heartbeat_update_required";
}
