namespace Heartbeat.Server.Entities;

public static class AppCatalogStartupModes
{
    public const string Normal = "normal";
    public const string RollbackCompatible = "rollback-compatible";
}

public sealed class AppCatalogState
{
    public int Id { get; set; } = 1;
    public int SchemaVersion { get; set; }
    public int CatalogVersion { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; set; }
    public string StartupMode { get; set; } = AppCatalogStartupModes.Normal;
}
