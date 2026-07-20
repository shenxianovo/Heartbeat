using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 知识裁决的确定性提交端（ADR-028 §5/§8，单位随 ADR-029 换 Matcher）：绑定 Strand / Mute，皆幂等。
    /// Dashboard → Analytics 的知识写路径；所有操作按 OwnerId 隔离。
    /// </summary>
    public class KnowledgeService(AppDbContext db, TimeProvider? clock = null)
    {
        private readonly AppDbContext _db = db;
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        /// <summary>
        /// 建/改 Strand：带 Id 按 Id 定位（可改名）；无 Id 按 (OwnerId, Name) 收敛，重复提交不产重复行。
        /// 成员整组替换；无效 Matcher（空 Source / 无有效步）剔除。带 Id 但查无此行（含跨 owner）返回 null。
        /// </summary>
        public async Task<StrandResponse?> BindStrandAsync(
            string ownerId, BindStrandRequest request, CancellationToken ct = default)
        {
            var name = request.Name.Trim();

            var strand = request.Id is { } id
                ? await _db.Strands.Include(s => s.Members)
                    .FirstOrDefaultAsync(s => s.Id == id && s.OwnerId == ownerId, ct)
                : await _db.Strands.Include(s => s.Members)
                    .FirstOrDefaultAsync(s => s.OwnerId == ownerId && s.Name == name, ct);

            if (request.Id != null && strand == null)
                return null;

            var now = _clock.GetUtcNow();
            if (strand == null)
            {
                strand = new Strand { Id = Guid.CreateVersion7(), OwnerId = ownerId, CreatedAt = now };
                _db.Strands.Add(strand);
            }

            strand.Name = name;
            strand.Gloss = request.Gloss.Trim();
            strand.UpdatedAt = now;

            // 成员整组替换（必需 FK ⇒ 孤儿即删）。规范化后按 (Source, StepsJson) 去重。
            strand.Members.Clear();
            var members = request.Members
                .Select(MatcherNormalizer.Normalize)
                .Where(m => m != null)
                .Select(m => (m!.Source, StepsJson: MatcherCodec.Serialize(m.Steps)))
                .Distinct();
            foreach (var (source, stepsJson) in members)
                strand.Members.Add(new StrandMatcher { Source = source, StepsJson = stepsJson });

            await _db.SaveChangesAsync(ct);
            return ToResponse(strand);
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
                OwnerId = ownerId,
                Source = normalized.Source,
                StepsJson = stepsJson,
                CreatedAt = _clock.GetUtcNow()
            });
            await _db.SaveChangesAsync(ct);
            return true;
        }

        private static StrandResponse ToResponse(Strand strand) => new()
        {
            Id = strand.Id,
            Name = strand.Name,
            Gloss = strand.Gloss,
            Members = strand.Members
                .Select(m => new MatcherDto { Source = m.Source, Steps = MatcherCodec.Deserialize(m.StepsJson) })
                .ToList(),
            CreatedAt = strand.CreatedAt,
            UpdatedAt = strand.UpdatedAt
        };
    }
}
