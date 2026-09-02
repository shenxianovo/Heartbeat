namespace Heartbeat.Collector.VRChat;

internal static class VRChatCollectorMetadata
{
    public const string PackageId = "heartbeat.collector.vrchat";

    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assemblyVersion = typeof(VRChatCollectorMetadata).Assembly.GetName().Version
                              ?? throw new InvalidOperationException("VRChat Collector assembly version is unavailable.");
        if (assemblyVersion.Build < 0)
            throw new InvalidOperationException("VRChat Collector assembly version must include a patch component.");
        return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }
}
