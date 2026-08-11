using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

/// <summary>
/// App 产品键切换的 C# 数据半边：把旧 system/app Matcher 中的进程展示值规范到已存在
/// App.Key。只接受能唯一解析到一个既有产品的别名，不据名称创建或合并任何 App。
/// </summary>
public static class AppKnowledgeBackfill
{
    public static async Task RunAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var apps = await db.Apps
            .Select(x => new
            {
                x.Key,
                x.DisplayName,
                Identities = x.Identities.Select(i => i.Key).ToList()
            })
            .ToListAsync(cancellationToken);
        if (apps.Count == 0) return;

        var aliasCandidates = apps
            .SelectMany(app => Aliases(app.Key, app.DisplayName, app.Identities)
                .Select(alias => (Alias: alias, app.Key)))
            .GroupBy(x => x.Alias, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

        var strandRows = await db.StrandMatchers.Where(x => x.Source == ActivitySources.System).ToListAsync(cancellationToken);
        var mutedRows = await db.MutedMatchers.Where(x => x.Source == ActivitySources.System).ToListAsync(cancellationToken);
        var probeRows = await db.RecurrenceProbes.Where(x => x.Source == ActivitySources.System).ToListAsync(cancellationToken);

        var strandChanged = Rewrite(strandRows, x => x.StepsJson, (x, json) => x.StepsJson = json, aliasCandidates);
        var mutedChanged = Rewrite(mutedRows, x => x.StepsJson, (x, json) => x.StepsJson = json, aliasCandidates);
        var probeChanged = Rewrite(probeRows, x => x.StepsJson, (x, json) => x.StepsJson = json, aliasCandidates);
        if (strandChanged.Count + mutedChanged.Count + probeChanged.Count == 0) return;

        db.StrandMatchers.RemoveRange(strandRows
            .GroupBy(x => (x.StrandId, x.Source, x.StepsJson))
            .SelectMany(g => g.OrderBy(x => x.Id).Skip(1)));
        db.MutedMatchers.RemoveRange(mutedRows
            .GroupBy(x => (x.OwnerId, x.Source, x.StepsJson))
            .SelectMany(g => g.OrderBy(x => x.Id).Skip(1)));
        db.RecurrenceProbes.RemoveRange(probeRows
            .GroupBy(x => (x.EpisodeId, x.Source, x.StepsJson))
            .SelectMany(g => RecurrenceProbeDeduplication.OrderForKeep(g).Skip(1)));

        var owners = mutedChanged.Select(x => x.OwnerId)
            .Concat(probeChanged.Select(x => x.OwnerId))
            .ToHashSet(StringComparer.Ordinal);
        var strandIds = strandChanged.Select(x => x.StrandId).Distinct().ToList();
        if (strandIds.Count > 0)
            owners.UnionWith(await db.Strands.Where(x => strandIds.Contains(x.Id)).Select(x => x.OwnerId).ToListAsync(cancellationToken));
        if (owners.Count > 0)
            db.DailyQuestionSets.RemoveRange(await db.DailyQuestionSets
                .Where(x => owners.Contains(x.OwnerId))
                .ToListAsync(cancellationToken));

        await db.SaveChangesAsync(cancellationToken);
    }

    private static List<T> Rewrite<T>(
        IEnumerable<T> rows,
        Func<T, string> jsonOf,
        Action<T, string> setJson,
        IReadOnlyDictionary<string, string> aliases)
    {
        var changed = new List<T>();
        foreach (var row in rows)
        {
            var steps = MatcherCodec.Deserialize(jsonOf(row));
            var touched = false;
            foreach (var step in steps)
            {
                if (step.Reading == "app" && aliases.TryGetValue(step.Value, out var appKey) && step.Value != appKey)
                {
                    step.Value = appKey;
                    touched = true;
                }
            }
            if (!touched) continue;
            var normalized = MatcherNormalizer.Normalize(new MatcherDto { Source = ActivitySources.System, Steps = steps })!;
            setJson(row, MatcherCodec.Serialize(normalized.Steps));
            changed.Add(row);
        }
        return changed;
    }

    private static IEnumerable<string> Aliases(string key, string displayName, IEnumerable<string> identities)
    {
        yield return key.ToLowerInvariant();
        yield return displayName.Trim().ToLowerInvariant();
        yield return AppIdentityKeys.ProductSlug(displayName);
        foreach (var identity in identities)
        {
            yield return identity;
            yield return identity[(identity.IndexOf(':') + 1)..];
        }
    }
}
