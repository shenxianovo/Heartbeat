using Heartbeat.Core.DTOs.Facts;

namespace Heartbeat.Core.Facts;

/// <summary>Evidence available in pre-Target Browser streams, shared by cache and ingest adapters.</summary>
public static class BrowserFactAttribution
{
    public static Guid? Observer(string? installationIdentity) =>
        Guid.TryParse(installationIdentity, out var id) && id != Guid.Empty ? id : null;

    public static FactTarget? Target(string deviceReference, string? appIdentityKey)
    {
        if (string.IsNullOrWhiteSpace(appIdentityKey)) return null;
        try { return ApplicationContextReference.Parse(new ApplicationContextReference(deviceReference, appIdentityKey).ToTarget().Reference).ToTarget(); }
        catch (ArgumentException) { return null; }
    }
}
