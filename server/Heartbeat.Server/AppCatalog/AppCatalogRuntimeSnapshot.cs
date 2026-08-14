namespace Heartbeat.Server.AppCatalog;

/// <summary>
/// Requests only consult the built-in Catalog after startup has proved that its snapshot is
/// compatible with the database. Rollback-compatible startup deliberately disables new
/// Catalog bindings: existing AppIdentity rows retain the newer database mapping, while an
/// older binary cannot recreate a superseded mapping for a previously unseen identity.
/// </summary>
public sealed class AppCatalogRuntimeSnapshot(AppCatalogSnapshot builtIn)
{
    private volatile bool _enabled;
    private volatile bool _rollbackCompatible;
    private IReadOnlyDictionary<string, AppCatalogProduct> _productsByIdentity =
        new Dictionary<string, AppCatalogProduct>(StringComparer.Ordinal);

    public bool IsEnabled => _enabled;
    public bool IsRollbackCompatible => _rollbackCompatible;

    public void Enable()
    {
        _productsByIdentity = builtIn.Document.Products
            .SelectMany(product => product.Identities.Select(identity => (identity, product)))
            .ToDictionary(x => x.identity, x => x.product, StringComparer.Ordinal);
        _enabled = true;
        _rollbackCompatible = false;
    }

    public void EnterRollbackCompatibility()
    {
        _enabled = false;
        _rollbackCompatible = true;
        _productsByIdentity = new Dictionary<string, AppCatalogProduct>(StringComparer.Ordinal);
    }

    public bool TryGetProduct(string identityKey, out AppCatalogProduct product)
    {
        if (_enabled && _productsByIdentity.TryGetValue(identityKey, out var resolved))
        {
            product = resolved;
            return true;
        }

        product = null!;
        return false;
    }
}
