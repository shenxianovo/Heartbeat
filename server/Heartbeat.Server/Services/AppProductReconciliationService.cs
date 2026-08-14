using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed record AppProductReconciliationResult(
    long TargetAppId,
    string TargetAppKey,
    IReadOnlyList<string> IdentityKeys,
    int LegacySegmentsRebound,
    int CurrentDevicesAffected,
    int AppsRemoved,
    int IconsMovedOrRemoved,
    int KnowledgeRowsRewritten,
    int QuestionCachesInvalidated);

/// <summary>
/// Identity-to-product desired-state mutation used by built-in Catalog and local Overrides.
/// The caller owns the surrounding transaction and advisory lock; this module owns every
/// dependent product mutation so mapping paths cannot drift apart.
/// </summary>
public sealed class AppProductReconciliationService(AppDbContext db)
{
    public async Task<AppProductReconciliationResult> ReconcileAsync(
        AppCatalogProduct product,
        IReadOnlyCollection<string> identityKeys,
        App? explicitTarget = null,
        bool markTargetFormal = true,
        IReadOnlySet<string>? preserveSourceAppKeys = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedKeys = identityKeys
            .Select(AppIdentityKeys.Normalize)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (normalizedKeys.Length == 0)
            return new(0, product.Key, [], 0, 0, 0, 0, 0, 0);

        var identities = await db.AppIdentities
            .Include(x => x.App)
            .Where(x => normalizedKeys.Contains(x.Key))
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);
        if (identities.Count == 0)
            return new(0, product.Key, normalizedKeys, 0, 0, 0, 0, 0, 0);

        var target = explicitTarget ?? await SelectTargetAsync(product, identities, cancellationToken);
        var oldTargetKey = target.Key;
        var oldTargetDisplayName = target.DisplayName;
        var sourceApps = identities.Select(x => x.App)
            .Where(x => x.Id != target.Id || target.Id == 0)
            .DistinctBy(x => x.Id)
            .ToList();

        if (target.Id == 0 && db.Entry(target).State == EntityState.Detached)
            db.Apps.Add(target);

        if (!string.Equals(target.Key, product.Key, StringComparison.Ordinal))
        {
            var collision = await db.Apps.SingleOrDefaultAsync(
                x => x.Key == product.Key && x.Id != target.Id, cancellationToken);
            if (collision is not null)
            {
                if (!sourceApps.Any(x => x.Id == collision.Id))
                    throw new AppCatalogException(
                        $"Cannot canonicalize App '{target.Key}' as '{product.Key}' because that key already exists.");

                // Preserve the established target Id even when a newer provisional product has
                // already claimed the canonical key. Free the unique key inside the same
                // transaction before renaming the formal target; the collision is absorbed below.
                collision.Key = $"catalog-absorbed-{collision.Id}";
                await db.SaveChangesAsync(cancellationToken);
            }
            target.Key = product.Key;
        }
        target.DisplayName = product.DisplayName;
        if (markTargetFormal) target.IsProvisional = false;
        if (target.Id != 0)
        {
            var targetOverrides = await db.AppCatalogOverrides
                .Where(x => x.Status == AppCatalogOverrideStatuses.Active && x.TargetAppId == target.Id)
                .ToListAsync(cancellationToken);
            foreach (var targetOverride in targetOverrides)
                targetOverride.TargetAppKey = target.Key;
        }

        var movedIdentityIds = identities.Select(x => x.Id).ToArray();
        foreach (var identity in identities) identity.App = target;

        var currentDevices = await db.Devices
            .Where(x => x.CurrentAppIdentityId != null && movedIdentityIds.Contains(x.CurrentAppIdentityId.Value))
            .ToListAsync(cancellationToken);
        foreach (var device in currentDevices) device.CurrentApp = product.DisplayName;

        var allSourceIds = sourceApps.Where(x => x.Id != 0).Select(x => x.Id).ToArray();
        var remainingCounts = allSourceIds.Length == 0
            ? new Dictionary<long, int>()
            : await db.AppIdentities
                .Where(x => allSourceIds.Contains(x.AppId) && !movedIdentityIds.Contains(x.Id))
                .GroupBy(x => x.AppId)
                .Select(x => new { AppId = x.Key, Count = x.Count() })
                .ToDictionaryAsync(x => x.AppId, x => x.Count, cancellationToken);

        var protectedTargetIds = await db.AppCatalogOverrides
            .Where(x => x.Status == AppCatalogOverrideStatuses.Active &&
                        x.TargetAppId != null && allSourceIds.Contains(x.TargetAppId.Value))
            .Select(x => x.TargetAppId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var drainedSources = sourceApps
            .Where(x => x.Id != 0)
            .Where(x => !remainingCounts.ContainsKey(x.Id))
            .Where(x => !protectedTargetIds.Contains(x.Id))
            .ToList();
        var removableSources = drainedSources
            .Where(x => preserveSourceAppKeys?.Contains(x.Key) != true)
            .ToList();
        var drainedSourceIds = drainedSources.Select(x => x.Id).ToArray();

        var legacySegments = await db.ActivitySegments
            .Include(x => x.Device)
            .Where(x =>
                x.AppIdentityId != null && movedIdentityIds.Contains(x.AppIdentityId.Value) ||
                x.AppIdentityId == null && x.AppId != null && drainedSourceIds.Contains(x.AppId.Value))
            .ToListAsync(cancellationToken);
        foreach (var segment in legacySegments) segment.App = target;

        var iconChanges = await ReconcileIconsAsync(target, drainedSources, cancellationToken);
        var aliases = drainedSources
            .SelectMany(x => ProductAliases(x.Key, x.DisplayName, x.Identities.Select(i => i.Key)))
            .ToHashSet(StringComparer.Ordinal);
        var authoritativeAliases = drainedSources.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (!string.Equals(oldTargetKey, target.Key, StringComparison.Ordinal))
        {
            aliases.UnionWith(ProductAliases(oldTargetKey, oldTargetDisplayName, identities.Select(x => x.Key)));
            authoritativeAliases.Add(oldTargetKey);
            aliases.Remove(target.Key);
        }
        await RemoveAmbiguousAliasesAsync(
            aliases,
            authoritativeAliases,
            drainedSources.Select(x => x.Id).Append(target.Id).ToHashSet(),
            cancellationToken);

        var knowledge = await RewriteKnowledgeAsync(aliases, target.Key, cancellationToken);
        var impactedOwners = await db.ActivitySegments
            .Where(x => x.AppIdentityId != null && movedIdentityIds.Contains(x.AppIdentityId.Value))
            .Select(x => x.Device.OwnerId)
            .Distinct()
            .ToListAsync(cancellationToken);
        impactedOwners.AddRange(legacySegments.Select(x => x.Device.OwnerId));
        impactedOwners.AddRange(currentDevices.Select(x => x.OwnerId));
        impactedOwners.AddRange(knowledge.ImpactedOwners);
        var ownerSet = impactedOwners.ToHashSet(StringComparer.Ordinal);
        var caches = ownerSet.Count == 0
            ? []
            : await db.DailyQuestionSets.Where(x => ownerSet.Contains(x.OwnerId)).ToListAsync(cancellationToken);
        db.DailyQuestionSets.RemoveRange(caches);
        db.Apps.RemoveRange(removableSources);

        return new(
            target.Id,
            target.Key,
            normalizedKeys,
            legacySegments.Count,
            currentDevices.Count,
            removableSources.Count,
            iconChanges,
            knowledge.Rewritten,
            caches.Count);
    }

    private async Task<App> SelectTargetAsync(
        AppCatalogProduct product,
        IReadOnlyList<AppIdentity> identities,
        CancellationToken cancellationToken)
    {
        var candidates = identities.Select(x => x.App).DistinctBy(x => x.Id).ToList();
        var byCanonicalKey = await db.Apps.SingleOrDefaultAsync(x => x.Key == product.Key, cancellationToken);
        if (byCanonicalKey is not null && !byCanonicalKey.IsProvisional) return byCanonicalKey;

        var formal = candidates.Where(x => !x.IsProvisional).OrderBy(x => x.Id).ToList();
        if (formal.Count == 1) return formal[0];
        if (formal.Count > 1)
            throw new AppCatalogException(
                $"Catalog product '{product.Key}' spans multiple established Apps: " +
                string.Join(", ", formal.Select(x => x.Key).Order(StringComparer.Ordinal)) + ".");
        if (byCanonicalKey is not null && candidates.Any(x => x.Id == byCanonicalKey.Id))
            return byCanonicalKey;
        return candidates.OrderBy(x => x.Id).First();
    }

    private async Task RemoveAmbiguousAliasesAsync(
        HashSet<string> aliases,
        HashSet<string> authoritativeAliases,
        HashSet<long> excludedAppIds,
        CancellationToken cancellationToken)
    {
        if (aliases.Count == 0) return;
        var otherProducts = await db.Apps
            .Where(x => !excludedAppIds.Contains(x.Id))
            .Select(x => new
            {
                x.Key,
                x.DisplayName,
                Identities = x.Identities.Select(i => i.Key).ToList()
            })
            .ToListAsync(cancellationToken);
        var aliasesOwnedElsewhere = otherProducts
            .SelectMany(x => ProductAliases(x.Key, x.DisplayName, x.Identities))
            .ToHashSet(StringComparer.Ordinal);
        aliases.RemoveWhere(x =>
            !authoritativeAliases.Contains(x) && aliasesOwnedElsewhere.Contains(x));
    }

    private async Task<int> ReconcileIconsAsync(
        App target,
        IReadOnlyList<App> removableSources,
        CancellationToken cancellationToken)
    {
        if (removableSources.Count == 0) return 0;
        var sourceIds = removableSources.Select(x => x.Id).ToArray();
        var icons = await db.AppIcons
            .Where(x => x.AppId == target.Id || sourceIds.Contains(x.AppId))
            .OrderBy(x => x.OwnerId).ThenBy(x => x.UpdatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var changes = 0;
        foreach (var group in icons.GroupBy(x => x.OwnerId))
        {
            var keep = group.FirstOrDefault(x => x.AppId == target.Id) ?? group.First();
            if (keep.AppId != target.Id)
            {
                keep.App = target;
                changes++;
            }
            var remove = group.Where(x => !ReferenceEquals(x, keep)).ToList();
            changes += remove.Count;
            db.AppIcons.RemoveRange(remove);
        }
        return changes;
    }

    private async Task<KnowledgeRewriteResult> RewriteKnowledgeAsync(
        HashSet<string> aliases,
        string targetKey,
        CancellationToken cancellationToken)
    {
        if (aliases.Count == 0) return new(0, []);
        var strandRows = await db.StrandMatchers.Where(x => x.Source == ActivitySources.System).ToListAsync(cancellationToken);
        var mutedRows = await db.MutedMatchers.Where(x => x.Source == ActivitySources.System).ToListAsync(cancellationToken);
        var probeRows = await db.RecurrenceProbes.Where(x => x.Source == ActivitySources.System).ToListAsync(cancellationToken);
        var impactedOwners = new HashSet<string>(StringComparer.Ordinal);
        var rewritten = 0;

        foreach (var row in strandRows)
        {
            if (!TryRewrite(row.StepsJson, aliases, targetKey, out var json)) continue;
            row.StepsJson = json;
            rewritten++;
            var owner = await db.Strands.Where(x => x.Id == row.StrandId).Select(x => x.OwnerId).SingleAsync(cancellationToken);
            impactedOwners.Add(owner);
        }
        foreach (var row in mutedRows)
        {
            if (!TryRewrite(row.StepsJson, aliases, targetKey, out var json)) continue;
            row.StepsJson = json;
            rewritten++;
            impactedOwners.Add(row.OwnerId);
        }
        foreach (var row in probeRows)
        {
            if (!TryRewrite(row.StepsJson, aliases, targetKey, out var json)) continue;
            row.StepsJson = json;
            rewritten++;
            impactedOwners.Add(row.OwnerId);
        }

        Deduplicate(strandRows, x => (x.StrandId, x.Source, x.StepsJson), x => x.Id,
            rows => rows.OrderBy(x => x.Id).First(), rows => db.StrandMatchers.RemoveRange(rows));
        Deduplicate(mutedRows, x => (x.OwnerId, x.Source, x.StepsJson), x => x.Id,
            rows => rows.OrderBy(x => x.Id).First(), rows => db.MutedMatchers.RemoveRange(rows));
        Deduplicate(probeRows, x => (x.EpisodeId, x.Source, x.StepsJson), x => x.Id,
            rows => RecurrenceProbeDeduplication.OrderForKeep(rows).First(), rows => db.RecurrenceProbes.RemoveRange(rows));
        return new(rewritten, impactedOwners.ToArray());
    }

    private static bool TryRewrite(string oldJson, HashSet<string> aliases, string targetKey, out string newJson)
    {
        var steps = MatcherCodec.Deserialize(oldJson);
        var touched = false;
        foreach (var step in steps)
        {
            if (step.Reading != "app" || !aliases.Contains(step.Value)) continue;
            step.Value = targetKey;
            touched = true;
        }
        if (!touched)
        {
            newJson = oldJson;
            return false;
        }
        var normalized = MatcherNormalizer.Normalize(new MatcherDto
        {
            Source = ActivitySources.System,
            Steps = steps
        })!;
        newJson = MatcherCodec.Serialize(normalized.Steps);
        return true;
    }

    private static void Deduplicate<T, TKey>(
        IEnumerable<T> rows,
        Func<T, TKey> key,
        Func<T, Guid> id,
        Func<IEnumerable<T>, T> selectKeep,
        Action<IEnumerable<T>> remove)
        where T : class
        where TKey : notnull
    {
        foreach (var group in rows.GroupBy(key).Where(x => x.Count() > 1))
        {
            var keep = selectKeep(group);
            remove(group.Where(x => !ReferenceEquals(x, keep)).OrderBy(id).ToList());
        }
    }

    private static IEnumerable<string> ProductAliases(
        string productKey,
        string displayName,
        IEnumerable<string> identityKeys)
    {
        yield return productKey.ToLowerInvariant();
        yield return displayName.Trim().ToLowerInvariant();
        yield return AppIdentityKeys.ProductSlug(displayName);
        foreach (var identityKey in identityKeys)
        {
            yield return identityKey;
            yield return identityKey[(identityKey.IndexOf(':') + 1)..];
        }
    }

    private sealed record KnowledgeRewriteResult(int Rewritten, IReadOnlyList<string> ImpactedOwners);
}
