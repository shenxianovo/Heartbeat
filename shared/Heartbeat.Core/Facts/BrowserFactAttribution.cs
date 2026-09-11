namespace Heartbeat.Core.Facts;

/// <summary>Evidence available in pre-Target Browser streams, shared by cache and ingest adapters.</summary>
public static class BrowserFactAttribution
{
    public static Guid? Observer(string? installationIdentity) =>
        Guid.TryParse(installationIdentity, out var id) && id != Guid.Empty ? id : null;
}
