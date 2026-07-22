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
        /// 裁决/编辑 Strand 的两条路径（ADR-029 §1：脊柱聚合、身体策展）：
        /// <para>· 无 Id = <b>归入</b>（问题卡的裁决动词——"这个观测属于 X"，非"X 是什么"）：
        /// 按 (OwnerId, lower(Name)) 收敛；已存在则成员并集追加、gloss 空则填非空不动、名字保留库中原形;
        /// 不存在则创建。指纹靠逐日裁决聚合生长——判官每卡只锚一个 Matcher，撞名归入是指纹的唯一生长通道。</para>
        /// <para>· 带 Id = <b>编辑</b>（未来编辑表单的契约）：可改名，gloss 覆盖，成员整组替换
        /// （必需 FK ⇒ 孤儿即删）。查无此行（含跨 owner）返回 null。</para>
        /// 无效 Matcher（空 Source / 无有效步）剔除；成员按 canonical 形去重。
        /// </summary>
        public async Task<StrandResponse?> BindStrandAsync(
            string ownerId, BindStrandRequest request, CancellationToken ct = default)
        {
            var name = request.Name.Trim();
            var lowerName = name.ToLowerInvariant();

            var strand = request.Id is { } id
                ? await _db.Strands.Include(s => s.Members)
                    .FirstOrDefaultAsync(s => s.Id == id && s.OwnerId == ownerId, ct)
                : await _db.Strands.Include(s => s.Members)
                    .FirstOrDefaultAsync(s => s.OwnerId == ownerId && s.Name.ToLower() == lowerName, ct);

            if (request.Id != null && strand == null)
                return null;

            var now = _clock.GetUtcNow();
            var isEdit = request.Id != null;
            if (strand == null)
            {
                strand = new Strand { Id = Guid.CreateVersion7(), OwnerId = ownerId, CreatedAt = now, Name = name };
                _db.Strands.Add(strand);
            }

            if (isEdit)
                strand.Name = name;

            // gloss：编辑覆盖；归入只在库里为空时补位——既有释义是用户亲口事实，
            // 不被后续卡片上（多半是 AI 提案的）gloss 静默碾压。
            var gloss = request.Gloss.Trim();
            if (isEdit || strand.Gloss.Length == 0)
                strand.Gloss = gloss;

            strand.UpdatedAt = now;

            var members = request.Members
                .Select(MatcherNormalizer.Normalize)
                .Where(m => m != null)
                .Select(m => (m!.Source, StepsJson: MatcherCodec.Serialize(m.Steps)))
                .Distinct();

            if (isEdit)
            {
                // 编辑 = 定义指纹形状：整组替换。
                strand.Members.Clear();
                foreach (var (source, stepsJson) in members)
                    strand.Members.Add(new StrandMatcher { Source = source, StepsJson = stepsJson });
            }
            else
            {
                // 归入 = 指纹并集追加：canonical 身份已存在的跳过。
                var existing = strand.Members
                    .Select(m => (m.Source, m.StepsJson))
                    .ToHashSet();
                foreach (var (source, stepsJson) in members)
                    if (existing.Add((source, stepsJson)))
                        strand.Members.Add(new StrandMatcher { Source = source, StepsJson = stepsJson });
            }

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
