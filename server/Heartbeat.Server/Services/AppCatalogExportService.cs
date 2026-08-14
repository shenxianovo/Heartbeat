using System.Security.Cryptography;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed class AppCatalogExportException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AppCatalogExportService(
    AppDbContext db,
    AppCatalogSnapshot builtInCatalog,
    AppCatalogRuntimeSnapshot? runtimeCatalog = null)
{
    public async Task<AppCatalogExportResponse> ExportAsync(
        AppCatalogExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (runtimeCatalog?.IsRollbackCompatible == true)
            throw new AppCatalogExportException(
                "catalog_rollback_compatibility",
                "A Catalog candidate cannot be exported while the server is in rollback compatibility mode.");

        var selectedKeys = NormalizeSelection(request.SelectedIdentityKeys);
        var proposedVersion = builtInCatalog.Document.CatalogVersion + 1;
        if (selectedKeys.Length == 0)
            return NoChanges(proposedVersion);

        var selectedOverrides = await db.AppCatalogOverrides
            .AsNoTracking()
            .Include(x => x.AppIdentity)
            .Include(x => x.TargetApp)
            .Where(x => x.Status == AppCatalogOverrideStatuses.Active &&
                        selectedKeys.Contains(x.AppIdentity.Key))
            .ToListAsync(cancellationToken);
        var foundKeys = selectedOverrides.Select(x => x.AppIdentity.Key).ToHashSet(StringComparer.Ordinal);
        var missing = selectedKeys.FirstOrDefault(x => !foundKeys.Contains(x));
        if (missing is not null)
            throw new AppCatalogExportException(
                "override_not_found", $"Active Override for '{missing}' was not found.");

        var products = builtInCatalog.Document.Products.ToDictionary(
            x => x.Key,
            x => new CandidateProduct(x.DisplayName, x.Identities.ToHashSet(StringComparer.Ordinal)),
            StringComparer.Ordinal);
        foreach (var localOverride in selectedOverrides.OrderBy(x => x.AppIdentity.Key, StringComparer.Ordinal))
        {
            var identityKey = localOverride.AppIdentity.Key;
            foreach (var product in products.Values) product.Identities.Remove(identityKey);
            if (!products.TryGetValue(localOverride.TargetAppKey, out var target))
            {
                target = new CandidateProduct(
                    localOverride.TargetApp?.DisplayName ?? localOverride.TargetAppKey,
                    new HashSet<string>(StringComparer.Ordinal));
                products.Add(localOverride.TargetAppKey, target);
            }
            target.Identities.Add(identityKey);
        }

        var emptyProduct = products.FirstOrDefault(x => x.Value.Identities.Count == 0);
        if (!string.IsNullOrEmpty(emptyProduct.Key))
            throw new AppCatalogExportException(
                "catalog_product_would_be_empty",
                $"Promoting the selection would leave Catalog product '{emptyProduct.Key}' without identities.");

        var mergedProducts = products
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new AppCatalogProduct(
                x.Key,
                x.Value.DisplayName,
                x.Value.Identities.Order(StringComparer.Ordinal).ToArray()))
            .ToArray();
        var comparison = AppCatalogLoader.SerializeCanonical(new AppCatalogDocument(
            builtInCatalog.Document.SchemaVersion,
            builtInCatalog.Document.CatalogVersion,
            mergedProducts));
        if (comparison.AsSpan().SequenceEqual(builtInCatalog.CanonicalBytes))
            return NoChanges(proposedVersion);

        var content = AppCatalogLoader.SerializeCanonical(new AppCatalogDocument(
            builtInCatalog.Document.SchemaVersion,
            proposedVersion,
            mergedProducts));
        return new AppCatalogExportResponse
        {
            HasChanges = true,
            SchemaVersion = builtInCatalog.Document.SchemaVersion,
            ProposedCatalogVersion = proposedVersion,
            FileName = $"app-catalog.v{proposedVersion}.candidate.json",
            ContentHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            Content = content
        };
    }

    private static string[] NormalizeSelection(IEnumerable<string>? values)
    {
        try
        {
            return (values ?? [])
                .Select(AppIdentityKeys.Normalize)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (ArgumentException exception)
        {
            throw new AppCatalogExportException("invalid_identity", exception.Message);
        }
    }

    private AppCatalogExportResponse NoChanges(int proposedVersion) => new()
    {
        HasChanges = false,
        SchemaVersion = builtInCatalog.Document.SchemaVersion,
        ProposedCatalogVersion = proposedVersion
    };

    private sealed record CandidateProduct(string DisplayName, HashSet<string> Identities);
}
