using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public sealed class AppMergeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// App 产品显式归并。dry-run 与 commit 共用 BuildPlanAsync；commit 在数据库事务和
/// advisory xact lock 内重新构建同一计划，避免预览/执行逻辑漂移与并发半归并。
/// </summary>
public class AppMergeService(AppDbContext db, TimeProvider? clock = null)
{
    private const string AdvisoryLockNamespace = "heartbeat.app-merge\n";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<AppMergeResponse> MergeAsync(
        AppMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceKey = NormalizeProductKey(request.SourceAppKey, nameof(request.SourceAppKey));
        var targetKey = NormalizeProductKey(request.TargetAppKey, nameof(request.TargetAppKey));
        if (sourceKey == targetKey)
            throw new AppMergeException("same_app", "Source and target App must be different.");

        if (request.DryRun)
        {
            if (await LoadReceiptAsync(sourceKey, targetKey, cancellationToken) is { } completed)
                return AsRetry(completed, dryRun: true);
            return (await BuildPlanAsync(sourceKey, targetKey, cancellationToken)).Response;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var productKey in new[] { sourceKey, targetKey }.Order(StringComparer.Ordinal))
        {
            var lockKey = AdvisoryLockNamespace + productKey;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
        }

        if (await LoadReceiptAsync(sourceKey, targetKey, cancellationToken) is { } receipt)
        {
            await transaction.CommitAsync(cancellationToken);
            return AsRetry(receipt, dryRun: false);
        }

        var plan = await BuildPlanAsync(sourceKey, targetKey, cancellationToken);
        Apply(plan);
        plan.Response.DryRun = false;
        plan.Response.Committed = true;
        plan.Response.Target.IsProvisional = false;

        db.AppMergeReceipts.Add(new AppMergeReceipt
        {
            SourceAppKey = sourceKey,
            TargetAppKey = targetKey,
            TargetAppId = plan.Target.Id,
            CompletedAt = _clock.GetUtcNow(),
            ResponseJson = JsonSerializer.Serialize(plan.Response)
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return plan.Response;
    }

    private async Task<MergePlan> BuildPlanAsync(
        string sourceKey,
        string targetKey,
        CancellationToken cancellationToken)
    {
        var apps = await db.Apps
            .Where(x => x.Key == sourceKey || x.Key == targetKey)
            .ToListAsync(cancellationToken);
        var source = apps.SingleOrDefault(x => x.Key == sourceKey)
            ?? throw new AppMergeException("source_not_found", $"Source App '{sourceKey}' was not found.");
        var target = apps.SingleOrDefault(x => x.Key == targetKey)
            ?? throw new AppMergeException("target_not_found", $"Target App '{targetKey}' was not found.");

        var identities = await db.AppIdentities
            .Where(x => x.AppId == source.Id)
            .OrderBy(x => x.Key)
            .ToListAsync(cancellationToken);
        var legacySegments = await db.ActivitySegments
            .Where(x => x.AppId == source.Id)
            .ToListAsync(cancellationToken);
        var currentDevices = await db.Devices
            .Where(x => x.CurrentAppIdentity != null && x.CurrentAppIdentity.AppId == source.Id)
            .ToListAsync(cancellationToken);
        var icons = await db.AppIcons
            .Where(x => x.AppId == source.Id || x.AppId == target.Id)
            .OrderBy(x => x.OwnerId).ThenBy(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);

        var aliases = ProductAliases(source.Key, source.DisplayName, identities.Select(x => x.Key));
        var otherProducts = await db.Apps
            .Where(x => x.Id != source.Id)
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
        // Source.Key 是权威引用且数据库全局唯一；其余旧表示只在无歧义时迁移。
        aliases.RemoveWhere(x => x != source.Key && aliasesOwnedElsewhere.Contains(x));
        var strandMatchers = await db.StrandMatchers
            .Where(x => x.Source == ActivitySources.System)
            .ToListAsync(cancellationToken);
        var mutedMatchers = await db.MutedMatchers
            .Where(x => x.Source == ActivitySources.System)
            .ToListAsync(cancellationToken);
        var probes = await db.RecurrenceProbes
            .Where(x => x.Source == ActivitySources.System)
            .ToListAsync(cancellationToken);
        var strandChanges = ProjectKnowledge(strandMatchers, x => x.StepsJson, aliases, target.Key);
        var mutedChanges = ProjectKnowledge(mutedMatchers, x => x.StepsJson, aliases, target.Key);
        var probeChanges = ProjectKnowledge(probes, x => x.StepsJson, aliases, target.Key);
        var strandDeduplications = ProjectDeduplications(
            strandMatchers, strandChanges, x => x.StepsJson,
            (x, json) => (x.StrandId, x.Source, json), x => x.Id,
            rows => rows.OrderBy(x => x.Id).First());
        var mutedDeduplications = ProjectDeduplications(
            mutedMatchers, mutedChanges, x => x.StepsJson,
            (x, json) => (x.OwnerId, x.Source, json), x => x.Id,
            rows => rows.OrderBy(x => x.Id).First());
        var probeDeduplications = ProjectDeduplications(
            probes, probeChanges, x => x.StepsJson,
            (x, json) => (x.EpisodeId, x.Source, json), x => x.Id,
            rows => RecurrenceProbeDeduplication.OrderForKeep(rows).First());

        var impactedOwners = (await db.ActivitySegments
                .Where(x => x.AppId == source.Id || x.AppIdentity != null && x.AppIdentity.AppId == source.Id)
                .Select(x => x.Device.OwnerId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .Concat(currentDevices.Select(x => x.OwnerId))
            .Concat(mutedChanges.Select(x => x.Row.OwnerId))
            .Concat(probeChanges.Select(x => x.Row.OwnerId))
            .ToHashSet(StringComparer.Ordinal);
        var affectedStrandIds = strandChanges.Select(x => x.Row.StrandId).Distinct().ToList();
        if (affectedStrandIds.Count > 0)
        {
            impactedOwners.UnionWith(await db.Strands
                .Where(x => affectedStrandIds.Contains(x.Id))
                .Select(x => x.OwnerId)
                .Distinct()
                .ToListAsync(cancellationToken));
        }
        var questionCaches = impactedOwners.Count == 0
            ? []
            : await db.DailyQuestionSets
                .Where(x => impactedOwners.Contains(x.OwnerId))
                .ToListAsync(cancellationToken);

        var iconImpacts = icons
            .GroupBy(x => x.OwnerId)
            .Select(g =>
            {
                var sourceIcon = g.Any(x => x.AppId == source.Id);
                var targetIcon = g.Any(x => x.AppId == target.Id);
                return new AppMergeIconImpact
                {
                    OwnerId = g.Key,
                    SourceIconExists = sourceIcon,
                    TargetIconExists = targetIcon,
                    Resolution = targetIcon ? "keep-target" : "move-source"
                };
            })
            .Where(x => x.SourceIconExists)
            .OrderBy(x => x.OwnerId)
            .ToList();

        return new MergePlan
        {
            Source = source,
            Target = target,
            Identities = identities,
            LegacySegments = legacySegments,
            CurrentDevices = currentDevices,
            Icons = icons,
            QuestionCaches = questionCaches,
            StrandChanges = strandChanges,
            MutedChanges = mutedChanges,
            ProbeChanges = probeChanges,
            StrandDeduplications = strandDeduplications,
            MutedDeduplications = mutedDeduplications,
            ProbeDeduplications = probeDeduplications,
            Response = new AppMergeResponse
            {
                DryRun = true,
                Source = ToInfo(source),
                Target = ToInfo(target),
                AppIdentityKeys = identities.Select(x => x.Key).ToList(),
                LegacySegmentsRebound = legacySegments.Count,
                CurrentDevicesAffected = currentDevices.Count,
                Icons = iconImpacts,
                Knowledge = new AppMergeKnowledgeImpact
                {
                    StrandMatchers = strandChanges.Count,
                    MutedMatchers = mutedChanges.Count,
                    RecurrenceProbes = probeChanges.Count,
                    QuestionCachesInvalidated = questionCaches.Count,
                    Changes = strandChanges.Select(x => ToImpact("strand-matcher", x.Row.Id, x))
                        .Concat(mutedChanges.Select(x => ToImpact("muted-matcher", x.Row.Id, x)))
                        .Concat(probeChanges.Select(x => ToImpact("recurrence-probe", x.Row.Id, x)))
                        .OrderBy(x => x.Category, StringComparer.Ordinal)
                        .ThenBy(x => x.RowId)
                        .ToList(),
                    Deduplications = strandDeduplications
                        .Select(x => ToDeduplication("strand-matcher", x, _ => null))
                        .Concat(mutedDeduplications.Select(x => ToDeduplication("muted-matcher", x, _ => null)))
                        .Concat(probeDeduplications.Select(x => ToDeduplication(
                            "recurrence-probe", x, row => row.Status)))
                        .OrderBy(x => x.Category, StringComparer.Ordinal)
                        .ThenBy(x => x.KeptRowId)
                        .ToList()
                },
                ProvisionalAppsRemoved = source.IsProvisional ? [ToInfo(source)] : []
            }
        };
    }

    private void Apply(MergePlan plan)
    {
        // 显式管理员归并即完成产品分类；即使目标原本也是一对一 provisional，也不再是未知产品。
        plan.Target.IsProvisional = false;
        foreach (var identity in plan.Identities) identity.AppId = plan.Target.Id;
        foreach (var segment in plan.LegacySegments) segment.AppId = plan.Target.Id;
        foreach (var device in plan.CurrentDevices) device.CurrentApp = plan.Target.DisplayName;

        foreach (var ownerIcons in plan.Icons.GroupBy(x => x.OwnerId))
        {
            var source = ownerIcons.FirstOrDefault(x => x.AppId == plan.Source.Id);
            if (source == null) continue;
            var target = ownerIcons.FirstOrDefault(x => x.AppId == plan.Target.Id);
            if (target != null) db.AppIcons.Remove(source);
            else source.AppId = plan.Target.Id;
        }

        ApplyKnowledge(
            plan.StrandChanges, plan.StrandDeduplications,
            (x, json) => x.StepsJson = json,
            rows => db.StrandMatchers.RemoveRange(rows));
        ApplyKnowledge(
            plan.MutedChanges, plan.MutedDeduplications,
            (x, json) => x.StepsJson = json,
            rows => db.MutedMatchers.RemoveRange(rows));
        ApplyKnowledge(
            plan.ProbeChanges, plan.ProbeDeduplications,
            (x, json) => x.StepsJson = json,
            rows => db.RecurrenceProbes.RemoveRange(rows));

        db.DailyQuestionSets.RemoveRange(plan.QuestionCaches);
        db.Apps.Remove(plan.Source);
    }

    private static List<KnowledgeChange<T>> ProjectKnowledge<T>(
        IEnumerable<T> rows,
        Func<T, string> jsonOf,
        HashSet<string> aliases,
        string targetKey)
    {
        var changes = new List<KnowledgeChange<T>>();
        foreach (var row in rows)
        {
            var oldJson = jsonOf(row);
            var steps = MatcherCodec.Deserialize(oldJson);
            var touched = false;
            foreach (var step in steps)
            {
                if (step.Reading == "app" && aliases.Contains(step.Value))
                {
                    step.Value = targetKey;
                    touched = true;
                }
            }
            if (touched)
            {
                var normalized = MatcherNormalizer.Normalize(new MatcherDto
                {
                    Source = ActivitySources.System,
                    Steps = steps
                })!;
                changes.Add(new KnowledgeChange<T>(row, oldJson, MatcherCodec.Serialize(normalized.Steps)));
            }
        }
        return changes;
    }

    private static List<KnowledgeDeduplication<T>> ProjectDeduplications<T, TKey>(
        IEnumerable<T> rows,
        List<KnowledgeChange<T>> changes,
        Func<T, string> jsonOf,
        Func<T, string, TKey> keyOf,
        Func<T, Guid> idOf,
        Func<IEnumerable<T>, T> selectKeep)
        where T : class
        where TKey : notnull
    {
        var rewritten = new Dictionary<T, string>(ReferenceEqualityComparer.Instance);
        foreach (var change in changes) rewritten.Add(change.Row, change.NewJson);
        return rows
            .GroupBy(x => keyOf(x, rewritten.TryGetValue(x, out var json) ? json : jsonOf(x)))
            .Select(group =>
            {
                var materialized = group.ToList();
                if (materialized.Count < 2) return null;
                var keep = selectKeep(materialized);
                return new KnowledgeDeduplication<T>(
                    keep,
                    materialized.Where(x => !ReferenceEquals(x, keep)).OrderBy(idOf).ToList());
            })
            .OfType<KnowledgeDeduplication<T>>()
            .ToList();
    }

    private static void ApplyKnowledge<T>(
        List<KnowledgeChange<T>> changes,
        List<KnowledgeDeduplication<T>> deduplications,
        Action<T, string> setJson,
        Action<IEnumerable<T>> remove)
    {
        foreach (var change in changes) setJson(change.Row, change.NewJson);
        remove(deduplications.SelectMany(x => x.Removed));
    }

    private async Task<AppMergeResponse?> LoadReceiptAsync(
        string sourceKey,
        string targetKey,
        CancellationToken cancellationToken)
    {
        var json = await db.AppMergeReceipts
            .Where(x => x.SourceAppKey == sourceKey && x.TargetAppKey == targetKey)
            .Select(x => x.ResponseJson)
            .SingleOrDefaultAsync(cancellationToken);
        return json == null ? null : JsonSerializer.Deserialize<AppMergeResponse>(json);
    }

    private static AppMergeResponse AsRetry(AppMergeResponse response, bool dryRun)
    {
        response.DryRun = dryRun;
        response.Committed = true;
        response.AlreadyMerged = true;
        return response;
    }

    private static HashSet<string> ProductAliases(
        string productKey,
        string displayName,
        IEnumerable<string> identityKeys)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal)
        {
            productKey.ToLowerInvariant(),
            displayName.Trim().ToLowerInvariant(),
            AppIdentityKeys.ProductSlug(displayName)
        };
        foreach (var identityKey in identityKeys)
        {
            aliases.Add(identityKey);
            aliases.Add(identityKey[(identityKey.IndexOf(':') + 1)..]);
        }
        return aliases;
    }

    private static string NormalizeProductKey(string key, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, parameterName);
        return key.Trim().ToLowerInvariant();
    }

    private static AppMergeAppInfo ToInfo(App app) => new()
    {
        Id = app.Id,
        Key = app.Key,
        DisplayName = app.DisplayName,
        IsProvisional = app.IsProvisional
    };

    private static AppMergeKnowledgeChange ToImpact<T>(
        string category,
        Guid rowId,
        KnowledgeChange<T> change) => new()
    {
        Category = category,
        RowId = rowId,
        BeforeStepsJson = change.OldJson,
        AfterStepsJson = change.NewJson
    };

    private static AppMergeKnowledgeDeduplication ToDeduplication<T>(
        string category,
        KnowledgeDeduplication<T> deduplication,
        Func<T, string?> statusOf) => new()
    {
        Category = category,
        KeptRowId = RowId(deduplication.Kept),
        RemovedRowIds = deduplication.Removed.Select(RowId).ToList(),
        KeptStatus = statusOf(deduplication.Kept)
    };

    private static Guid RowId<T>(T row) => row switch
    {
        StrandMatcher matcher => matcher.Id,
        MutedMatcher matcher => matcher.Id,
        RecurrenceProbe probe => probe.Id,
        _ => throw new InvalidOperationException($"Unsupported knowledge row type {typeof(T).Name}.")
    };

    private sealed class MergePlan
    {
        public required App Source { get; init; }
        public required App Target { get; init; }
        public required List<AppIdentity> Identities { get; init; }
        public required List<ActivitySegment> LegacySegments { get; init; }
        public required List<Device> CurrentDevices { get; init; }
        public required List<AppIcon> Icons { get; init; }
        public required List<DailyQuestionSet> QuestionCaches { get; init; }
        public required List<KnowledgeChange<StrandMatcher>> StrandChanges { get; init; }
        public required List<KnowledgeChange<MutedMatcher>> MutedChanges { get; init; }
        public required List<KnowledgeChange<RecurrenceProbe>> ProbeChanges { get; init; }
        public required List<KnowledgeDeduplication<StrandMatcher>> StrandDeduplications { get; init; }
        public required List<KnowledgeDeduplication<MutedMatcher>> MutedDeduplications { get; init; }
        public required List<KnowledgeDeduplication<RecurrenceProbe>> ProbeDeduplications { get; init; }
        public required AppMergeResponse Response { get; init; }
    }

    private sealed record KnowledgeChange<T>(T Row, string OldJson, string NewJson);
    private sealed record KnowledgeDeduplication<T>(T Kept, List<T> Removed);
}
