namespace Heartbeat.Desktop.Mac.Identity;

public interface IMacPlatformUuid
{
    string? Read();
}

public sealed class MacMachineIdentity(IMacPlatformUuid platformUuid)
{
    private readonly Lazy<string> _hardwareId = new(() =>
    {
        var value = platformUuid.Read()?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("macOS IOPlatformUUID is unavailable.")
            : value;
    });

    public string HardwareId => _hardwareId.Value;
}
