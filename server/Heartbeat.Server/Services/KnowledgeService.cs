using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 知识裁决的确定性提交端（ADR-028 §5/§8）：绑定 Strand / Mute 把手，皆幂等。
    /// Dashboard → Analytics 的知识写路径；所有操作按 OwnerId 隔离。
    /// </summary>
    public class KnowledgeService(AppDbContext db, TimeProvider? clock = null)
    {
        private readonly AppDbContext _db = db;
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        /// <summary>
        /// 建/改 Strand：带 Id 按 Id 定位（可改名）；无 Id 按 (OwnerId, Name) 收敛，重复提交不产重复行。
        /// 成员整组替换。带 Id 但查无此行（含跨 owner）返回 null。
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

            // 成员整组替换（必需 FK ⇒ 孤儿即删）。
            strand.Members.Clear();
            var members = request.Members
                .Select(m => (Source: m.Source.Trim(), Token: m.Token.Trim()))
                .Where(m => m.Source.Length > 0 && m.Token.Length > 0)
                .Distinct();
            foreach (var (source, token) in members)
                strand.Members.Add(new StrandHandle { Source = source, Token = token });

            await _db.SaveChangesAsync(ct);
            return ToResponse(strand);
        }

        /// <summary>Mute 一个把手：已静音即无事发生（幂等）。</summary>
        public async Task MuteHandleAsync(
            string ownerId, string source, string token, CancellationToken ct = default)
        {
            source = source.Trim();
            token = token.Trim();

            var exists = await _db.MutedHandles.AnyAsync(
                m => m.OwnerId == ownerId && m.Source == source && m.Token == token, ct);
            if (exists) return;

            _db.MutedHandles.Add(new MutedHandle
            {
                OwnerId = ownerId,
                Source = source,
                Token = token,
                CreatedAt = _clock.GetUtcNow()
            });
            await _db.SaveChangesAsync(ct);
        }

        private static StrandResponse ToResponse(Strand strand) => new()
        {
            Id = strand.Id,
            Name = strand.Name,
            Gloss = strand.Gloss,
            Members = strand.Members
                .Select(m => new HandleDto { Source = m.Source, Token = m.Token })
                .ToList(),
            CreatedAt = strand.CreatedAt,
            UpdatedAt = strand.UpdatedAt
        };
    }
}
