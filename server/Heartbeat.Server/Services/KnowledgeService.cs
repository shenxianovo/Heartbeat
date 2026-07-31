using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>Strand 写操作被拒的机器可判代码（ADR-031 §2/§6）。控制器按此映射 HTTP 状态。</summary>
    public static class KnowledgeErrorCodes
    {
        public const string InvalidName = "invalid_name";
        public const string InvalidDates = "invalid_dates";
        public const string ParentNotFound = "parent_not_found";
        public const string Cycle = "cycle";
        public const string Overlap = "overlap";
        public const string OutsideParentRange = "outside_parent_range";
        public const string ChildrenOutsideRange = "children_outside_range";
        public const string ActiveChildren = "active_children";
        public const string NotFound = "not_found";
        public const string VersionConflict = "version_conflict";
    }

    /// <summary>Strand 写操作结果：Strand 与 Error 互斥。</summary>
    public sealed record KnowledgeResult(StrandResponse? Strand, KnowledgeErrorResponse? Error)
    {
        public static KnowledgeResult Ok(StrandResponse strand) => new(strand, null);

        public static KnowledgeResult Fail(string code, string message, List<StrandBriefResponse>? strands = null)
            => new(null, new KnowledgeErrorResponse { Code = code, Message = message, Strands = strands ?? [] });
    }

    /// <summary>
    /// 知识写模型的确定性提交端（ADR-028 §5，树/日期/身份随 ADR-031）：
    /// Strand 是严格单父级、带近似本地日期范围的语境树，全部写操作按 UUIDv7 ID 定位——
    /// 按名收敛的旧"归入"语义已退役。所有不变量（无环、日期序、同父同名不重叠、父子包含、
    /// Owner 隔离）在此层统一校验；更新携带并发版本，陈旧提案返回冲突而非覆盖（§6）。
    /// </summary>
    public class KnowledgeService(AppDbContext db, TimeProvider? clock = null)
    {
        private readonly AppDbContext _db = db;
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        /// <summary>新建 Strand：显式父级（null = 顶层），成员规范化去重。父可零 Matcher。</summary>
        public async Task<KnowledgeResult> CreateStrandAsync(
            string ownerId, CreateStrandRequest request, CancellationToken ct = default)
        {
            var name = request.Name.Trim();
            if (name.Length == 0)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.InvalidName, "Name is required.");
            if (IsStartAfterEnd(request.StartedOn, request.EndedOn))
                return KnowledgeResult.Fail(KnowledgeErrorCodes.InvalidDates, "StartedOn must not be after EndedOn.");

            Strand? parent = null;
            if (request.ParentStrandId is { } parentId)
            {
                parent = await _db.Strands
                    .FirstOrDefaultAsync(s => s.Id == parentId && s.OwnerId == ownerId, ct);
                if (parent == null)
                    return KnowledgeResult.Fail(KnowledgeErrorCodes.ParentNotFound, "Parent strand not found.");
                if (IsOutsideParent(request.StartedOn, request.EndedOn, parent))
                    return KnowledgeResult.Fail(KnowledgeErrorCodes.OutsideParentRange,
                        "Known dates must stay within the parent's known range.");
            }

            var normalizedName = name.ToLowerInvariant();
            var overlapping = await FindOverlappingSiblingsAsync(
                ownerId, request.ParentStrandId, normalizedName, request.StartedOn, request.EndedOn, excludeId: null, ct);
            if (overlapping.Count > 0)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.Overlap,
                    "A sibling strand with the same name has an overlapping date range.", overlapping);

            var now = _clock.GetUtcNow();
            var strand = new Strand
            {
                Id = Guid.CreateVersion7(),
                OwnerId = ownerId,
                ParentStrandId = request.ParentStrandId,
                Name = name,
                NormalizedName = normalizedName,
                Gloss = request.Gloss.Trim(),
                StartedOn = request.StartedOn,
                EndedOn = request.EndedOn,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            ReplaceMembers(strand, request.Members);
            _db.Strands.Add(strand);
            await _db.SaveChangesAsync(ct);

            return KnowledgeResult.Ok(await ToResponseAsync(strand, ct));
        }

        /// <summary>编辑 Strand（按 Id）：名字/释义/日期覆盖，成员整组替换（孤儿即删）。</summary>
        public async Task<KnowledgeResult> UpdateStrandAsync(
            string ownerId, Guid id, UpdateStrandRequest request, CancellationToken ct = default)
        {
            var strand = await _db.Strands.Include(s => s.Members)
                .FirstOrDefaultAsync(s => s.Id == id && s.OwnerId == ownerId, ct);
            if (strand == null)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.NotFound, "Strand not found.");
            if (strand.Version != request.ExpectedVersion)
                return VersionConflict();

            var name = request.Name.Trim();
            if (name.Length == 0)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.InvalidName, "Name is required.");
            if (IsStartAfterEnd(request.StartedOn, request.EndedOn))
                return KnowledgeResult.Fail(KnowledgeErrorCodes.InvalidDates, "StartedOn must not be after EndedOn.");

            if (strand.ParentStrandId is { } parentId)
            {
                var parent = await _db.Strands.FirstAsync(s => s.Id == parentId, ct);
                if (IsOutsideParent(request.StartedOn, request.EndedOn, parent))
                    return KnowledgeResult.Fail(KnowledgeErrorCodes.OutsideParentRange,
                        "Known dates must stay within the parent's known range.");
            }

            var strayChildren = await FindChildrenOutsideRangeAsync(strand.Id, request.StartedOn, request.EndedOn, ct);
            if (strayChildren.Count > 0)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.ChildrenOutsideRange,
                    "Children have known dates outside the new range.", strayChildren);

            var normalizedName = name.ToLowerInvariant();
            var overlapping = await FindOverlappingSiblingsAsync(
                ownerId, strand.ParentStrandId, normalizedName, request.StartedOn, request.EndedOn, strand.Id, ct);
            if (overlapping.Count > 0)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.Overlap,
                    "A sibling strand with the same name has an overlapping date range.", overlapping);

            strand.Name = name;
            strand.NormalizedName = normalizedName;
            strand.Gloss = request.Gloss.Trim();
            strand.StartedOn = request.StartedOn;
            strand.EndedOn = request.EndedOn;
            strand.Members.Clear();
            ReplaceMembers(strand, request.Members);
            return await CommitAsync(strand, ct);
        }

        /// <summary>
        /// 移动 Strand = 纠正过去的错误理解（ADR-031 §2）：改写该节点及后代的历史层级解释。
        /// 现实归属变化不走这里——调用方结束旧节点、在新父级下创建新节点。
        /// </summary>
        public async Task<KnowledgeResult> MoveStrandAsync(
            string ownerId, Guid id, MoveStrandRequest request, CancellationToken ct = default)
        {
            var strand = await _db.Strands.Include(s => s.Members)
                .FirstOrDefaultAsync(s => s.Id == id && s.OwnerId == ownerId, ct);
            if (strand == null)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.NotFound, "Strand not found.");
            if (strand.Version != request.ExpectedVersion)
                return VersionConflict();

            if (request.NewParentStrandId is { } newParentId)
            {
                var parents = await ParentMapAsync(ownerId, ct);
                if (!parents.ContainsKey(newParentId))
                    return KnowledgeResult.Fail(KnowledgeErrorCodes.ParentNotFound, "Parent strand not found.");

                // 无环：新父级不得是自身或后代（沿新父级向根走，途经自身即环）。
                for (Guid? cursor = newParentId; cursor != null; cursor = parents.GetValueOrDefault(cursor.Value))
                    if (cursor == strand.Id)
                        return KnowledgeResult.Fail(KnowledgeErrorCodes.Cycle,
                            "Cannot move a strand under itself or its descendant.");

                var parent = await _db.Strands.FirstAsync(s => s.Id == newParentId, ct);
                if (IsOutsideParent(strand.StartedOn, strand.EndedOn, parent))
                    return KnowledgeResult.Fail(KnowledgeErrorCodes.OutsideParentRange,
                        "Known dates must stay within the new parent's known range.");
            }

            var overlapping = await FindOverlappingSiblingsAsync(
                ownerId, request.NewParentStrandId, strand.NormalizedName, strand.StartedOn, strand.EndedOn, strand.Id, ct);
            if (overlapping.Count > 0)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.Overlap,
                    "A sibling strand with the same name has an overlapping date range.", overlapping);

            strand.ParentStrandId = request.NewParentStrandId;
            return await CommitAsync(strand, ct);
        }

        /// <summary>
        /// 结束 Strand：设定近似终点。仍有活跃子节点（无终点或终点晚于此日）时返回显式冲突
        /// 及子节点清单，不静默级联（ADR-031 §2）——由调用方选择先结束子级、建立后继或整理层级。
        /// </summary>
        public async Task<KnowledgeResult> EndStrandAsync(
            string ownerId, Guid id, EndStrandRequest request, CancellationToken ct = default)
        {
            var strand = await _db.Strands.Include(s => s.Members)
                .FirstOrDefaultAsync(s => s.Id == id && s.OwnerId == ownerId, ct);
            if (strand == null)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.NotFound, "Strand not found.");
            if (strand.Version != request.ExpectedVersion)
                return VersionConflict();
            if (IsStartAfterEnd(strand.StartedOn, request.EndedOn))
                return KnowledgeResult.Fail(KnowledgeErrorCodes.InvalidDates, "EndedOn must not be before StartedOn.");

            if (strand.ParentStrandId is { } parentId)
            {
                var parent = await _db.Strands.FirstAsync(s => s.Id == parentId, ct);
                if (IsOutsideParent(strand.StartedOn, request.EndedOn, parent))
                    return KnowledgeResult.Fail(KnowledgeErrorCodes.OutsideParentRange,
                        "EndedOn must stay within the parent's known range.");
            }

            var activeChildren = await _db.Strands
                .Where(s => s.ParentStrandId == strand.Id)
                .Where(s => s.EndedOn == null || s.EndedOn > request.EndedOn)
                .ToListAsync(ct);
            if (activeChildren.Count > 0)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.ActiveChildren,
                    "Strand still has active children.", activeChildren.Select(ToBrief).ToList());

            var overlapping = await FindOverlappingSiblingsAsync(
                ownerId, strand.ParentStrandId, strand.NormalizedName, strand.StartedOn, request.EndedOn, strand.Id, ct);
            if (overlapping.Count > 0)
                return KnowledgeResult.Fail(KnowledgeErrorCodes.Overlap,
                    "A sibling strand with the same name has an overlapping date range.", overlapping);

            strand.EndedOn = request.EndedOn;
            return await CommitAsync(strand, ct);
        }

        /// <summary>
        /// 整树读取：该 Owner 全部节点（含已结束时期），带稳定 parent ID 与根到自身的 path。
        /// 展示消歧（同父同名不同时期）靠 path + 日期，树的组装归调用方。
        /// </summary>
        public async Task<List<StrandResponse>> GetStrandsAsync(string ownerId, CancellationToken ct = default)
        {
            var strands = await _db.Strands.Include(s => s.Members)
                .Where(s => s.OwnerId == ownerId)
                .OrderBy(s => s.Id)
                .ToListAsync(ct);
            var byId = strands.ToDictionary(s => s.Id);
            return strands.Select(s => ToResponse(s, PathOf(s, byId))).ToList();
        }

        /// <summary>
        /// Mute 一个 Matcher：已静音即无事发生（幂等，步骤顺序无关——规范化收敛）。
        /// 无效 Matcher 返回 false（由端点映射 400）。
        /// </summary>
        public async Task<bool> MuteMatcherAsync(
            string ownerId, MatcherDto matcher, CancellationToken ct = default)
        {
            if (MatcherNormalizer.Normalize(matcher) is not { } normalized)
                return false;

            var stepsJson = MatcherCodec.Serialize(normalized.Steps);
            var exists = await _db.MutedMatchers.AnyAsync(
                m => m.OwnerId == ownerId && m.Source == normalized.Source && m.StepsJson == stepsJson, ct);
            if (exists) return true;

            _db.MutedMatchers.Add(new MutedMatcher
            {
                Id = Guid.CreateVersion7(),
                OwnerId = ownerId,
                Source = normalized.Source,
                StepsJson = stepsJson,
                CreatedAt = _clock.GetUtcNow()
            });
            await _db.SaveChangesAsync(ct);
            return true;
        }

        /// <summary>版本 +1 并落库；并发 token（Version）失守时映射为 version_conflict——陈旧提案不覆盖新编辑。</summary>
        private async Task<KnowledgeResult> CommitAsync(Strand strand, CancellationToken ct)
        {
            strand.Version++;
            strand.UpdatedAt = _clock.GetUtcNow();
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return VersionConflict();
            }
            return KnowledgeResult.Ok(await ToResponseAsync(strand, ct));
        }

        private static KnowledgeResult VersionConflict()
            => KnowledgeResult.Fail(KnowledgeErrorCodes.VersionConflict,
                "Strand was modified since it was read. Reload and retry.");

        /// <summary>成员规范化 + canonical 去重后整组写入。无效 Matcher（空 Source / 无有效步）剔除。</summary>
        private static void ReplaceMembers(Strand strand, IEnumerable<MatcherDto> members)
        {
            var normalized = members
                .Select(MatcherNormalizer.Normalize)
                .Where(m => m != null)
                .Select(m => (m!.Source, StepsJson: MatcherCodec.Serialize(m.Steps)))
                .Distinct();
            foreach (var (source, stepsJson) in normalized)
                strand.Members.Add(new StrandMatcher
                {
                    Id = Guid.CreateVersion7(),
                    Source = source,
                    StepsJson = stepsJson,
                });
        }

        private static bool IsStartAfterEnd(DateOnly? start, DateOnly? end)
            => start != null && end != null && start > end;

        /// <summary>未知端点视为向对应方向无界（ADR-031 §2）。</summary>
        private static bool RangesOverlap(DateOnly? aStart, DateOnly? aEnd, DateOnly? bStart, DateOnly? bEnd)
            => (aStart ?? DateOnly.MinValue) <= (bEnd ?? DateOnly.MaxValue)
               && (bStart ?? DateOnly.MinValue) <= (aEnd ?? DateOnly.MaxValue);

        /// <summary>子级已知日期端点不得越出父级已知范围；未知端点不构成越界（ADR-031 §2）。</summary>
        private static bool IsOutsideParent(DateOnly? childStart, DateOnly? childEnd, Strand parent)
            => childStart != null && (childStart < parent.StartedOn || childStart > parent.EndedOn)
               || childEnd != null && (childEnd > parent.EndedOn || childEnd < parent.StartedOn);

        private async Task<List<StrandBriefResponse>> FindOverlappingSiblingsAsync(
            string ownerId, Guid? parentId, string normalizedName,
            DateOnly? startedOn, DateOnly? endedOn, Guid? excludeId, CancellationToken ct)
        {
            var siblings = await _db.Strands
                .Where(s => s.OwnerId == ownerId
                            && s.ParentStrandId == parentId
                            && s.NormalizedName == normalizedName
                            && s.Id != excludeId)
                .ToListAsync(ct);
            return siblings
                .Where(s => RangesOverlap(startedOn, endedOn, s.StartedOn, s.EndedOn))
                .Select(ToBrief)
                .ToList();
        }

        private async Task<List<StrandBriefResponse>> FindChildrenOutsideRangeAsync(
            Guid parentId, DateOnly? startedOn, DateOnly? endedOn, CancellationToken ct)
        {
            var children = await _db.Strands.Where(s => s.ParentStrandId == parentId).ToListAsync(ct);
            var probe = new Strand { StartedOn = startedOn, EndedOn = endedOn };
            return children
                .Where(c => IsOutsideParent(c.StartedOn, c.EndedOn, probe))
                .Select(ToBrief)
                .ToList();
        }

        private async Task<Dictionary<Guid, Guid?>> ParentMapAsync(string ownerId, CancellationToken ct)
            => await _db.Strands
                .Where(s => s.OwnerId == ownerId)
                .Select(s => new { s.Id, s.ParentStrandId })
                .ToDictionaryAsync(s => s.Id, s => s.ParentStrandId, ct);

        private async Task<StrandResponse> ToResponseAsync(Strand strand, CancellationToken ct)
        {
            var names = new List<string>();
            for (var cursor = strand; cursor != null;)
            {
                names.Insert(0, cursor.Name);
                cursor = cursor.ParentStrandId is { } pid
                    ? await _db.Strands.AsNoTracking().FirstAsync(s => s.Id == pid, ct)
                    : null;
            }
            return ToResponse(strand, names);
        }

        private static List<string> PathOf(Strand strand, Dictionary<Guid, Strand> byId)
        {
            var names = new List<string>();
            for (var cursor = strand; cursor != null;)
            {
                names.Insert(0, cursor.Name);
                cursor = cursor.ParentStrandId is { } pid ? byId.GetValueOrDefault(pid) : null;
            }
            return names;
        }

        private static StrandBriefResponse ToBrief(Strand s) => new()
        {
            Id = s.Id,
            Name = s.Name,
            StartedOn = s.StartedOn,
            EndedOn = s.EndedOn,
        };

        private static StrandResponse ToResponse(Strand strand, List<string> path) => new()
        {
            Id = strand.Id,
            ParentStrandId = strand.ParentStrandId,
            Name = strand.Name,
            Gloss = strand.Gloss,
            StartedOn = strand.StartedOn,
            EndedOn = strand.EndedOn,
            Path = path,
            Version = strand.Version,
            Members = strand.Members
                .Select(m => new MatcherDto { Source = m.Source, Steps = MatcherCodec.Deserialize(m.StepsJson) })
                .ToList(),
            CreatedAt = strand.CreatedAt,
            UpdatedAt = strand.UpdatedAt
        };
    }
}
