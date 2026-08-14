namespace Heartbeat.Server.AppCatalog;

public sealed record AppCatalogDocument(
    int SchemaVersion,
    int CatalogVersion,
    IReadOnlyList<AppCatalogProduct> Products);

public sealed record AppCatalogProduct(
    string Key,
    string DisplayName,
    IReadOnlyList<string> Identities);

public sealed record AppCatalogSnapshot(
    AppCatalogDocument Document,
    byte[] CanonicalBytes,
    string ContentHash);

public sealed class AppCatalogException(string message) : Exception(message);
