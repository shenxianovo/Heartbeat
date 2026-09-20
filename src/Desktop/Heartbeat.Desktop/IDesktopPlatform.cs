using Heartbeat.Collector.Desktop;

namespace Heartbeat.Desktop;

public interface IDesktopPlatform
{
    string CollectorKey { get; }
    string DisplayName { get; }
    string GetTarget();
    TimeProvider Clock { get; }
    IDesktopObservationSource CreateObservationSource();
    ICredentialStore Credentials { get; }
    void OpenPermissionSettings(ObservationCapability capability);
}

public interface ICredentialStore
{
    string? Read(string account);
    void Write(string account, string secret);
}
