using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>Episode / Probe 写操作被拒的机器可判代码（ADR-031 §4/§5）。</summary>
    public static class EpisodeErrorCodes
    {
        public const string InvalidText = "invalid_text";
        public const string InvalidTimes = "invalid_times";
        public const string StrandNotFound = "strand_not_found";
        public const string InvalidMatcher = "invalid_matcher";
        public const string ProbeResolved = "probe_resolved";
        public const string InvalidResolution = "invalid_resolution";
        public const string InvalidPromotion = "invalid_promotion";
        public const string NotFound = KnowledgeErrorCodes.NotFound;
        public const string VersionConflict = KnowledgeErrorCodes.VersionConflict;
    }

    /// <summary>Episode 写操作结果：Episode 与 Error 互斥。</summary>
    public sealed record EpisodeResult(EpisodeResponse? Episode, KnowledgeErrorResponse? Error)
    {
        public static EpisodeResult Ok(EpisodeResponse episode) => new(episode, null);

        public static EpisodeResult Fail(string code, string message)
            => new(null, new KnowledgeErrorResponse { Code = code, Message = message });
    }

    /// <summary>Probe 写操作结果。</summary>
    public sealed record ProbeResult(ProbeResponse? Probe, KnowledgeErrorResponse? Error)
    {
        public static ProbeResult Ok(ProbeResponse probe) => new(probe, null);

        public static ProbeResult Fail(string code, string message)
            => new(null, new KnowledgeErrorResponse { Code = code, Message = message });
    }

    /// <summary>提升事务结果。Error 携带 KnowledgeService 的冲突清单（如同名重叠）。</summary>
    public sealed record PromotionResult(PromoteEpisodeResponse? Promotion, KnowledgeErrorResponse? Error)
    {
        public static PromotionResult Ok(PromoteEpisodeResponse promotion) => new(promotion, null);

        public static PromotionResult Fail(string code, string message)
            => new(null, new KnowledgeErrorResponse { Code = code, Message = message });
    }

    /// <summary>
    /// Episode 与 RecurrenceProbe 的确定性提交端（ADR-031 §4/§5）：
    /// Episode 只接受用户确认后的写请求——本服务是唯一创建路径，摄入 / Matcher 命中 /
    /// Probe 命中都没有到这里的调用边。Probe 复用 Matcher 的 canonicalization 但不复用
    /// 其后果；提升是保留 Episode 的非破坏性事务，任何失败整批回滚。
    /// </summary>
    public class EpisodeService(AppDbContext db, KnowledgeService knowledgeService, TimeProvider? clock = null)
    {
        private readonly AppDbContext _db = db;
        private readonly KnowledgeService _knowledgeService = knowledgeService;
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        // ===== Episode =====

        public async Task<EpisodeResult> CreateEpisodeAsync(
            string ownerId, CreateEpisodeRequest request, CancellationToken ct = default)
        {
            var text = request.Text.Trim();
            if (text.Length == 0)
                return EpisodeResult.Fail(EpisodeErrorCodes.InvalidText, "Text is required.");
            if (ValidateTimes(request.LocalDate, request.ApproximateStart, request.ApproximateEnd) is { } timeError)
                return timeError;

            if (request.RelatedStrandId is { } strandId
                && !await _db.Strands.AnyAsync(s => s.Id == strandId && s.OwnerId == ownerId, ct))
                return EpisodeResult.Fail(EpisodeErrorCodes.StrandNotFound, "Related strand not found.");

            var now = _clock.GetUtcNow();
            var episode = new Episode
            {
                Id = Guid.CreateVersion7(),
                OwnerId = ownerId,
                LocalDate = request.LocalDate,
                Text = text,
                // 校验按客户端 offset 解释；落库统一 UTC（Npgsql timestamptz 契约）。
                ApproximateStart = request.ApproximateStart?.ToUniversalTime(),
                ApproximateEnd = request.ApproximateEnd?.ToUniversalTime(),
                RelatedStrandId = request.RelatedStrandId,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Episodes.Add(episode);
            await _db.SaveChangesAsync(ct);

            return EpisodeResult.Ok(await ToResponseAsync(episode, ct));
        }

        public async Task<EpisodeResult> UpdateEpisodeAsync(
            string ownerId, Guid id, UpdateEpisodeRequest request, CancellationToken ct = default)
        {
            var episode = await FindEpisodeAsync(ownerId, id, ct);
            if (episode == null)
                return EpisodeResult.Fail(EpisodeErrorCodes.NotFound, "Episode not found.");
            if (episode.Version != request.ExpectedVersion)
                return VersionConflict();

            var text = request.Text.Trim();
            if (text.Length == 0)
                return EpisodeResult.Fail(EpisodeErrorCodes.InvalidText, "Text is required.");
            if (ValidateTimes(request.LocalDate, request.ApproximateStart, request.ApproximateEnd) is { } timeError)
                return timeError;

            episode.LocalDate = request.LocalDate;
            episode.Text = text;
            episode.ApproximateStart = request.ApproximateStart?.ToUniversalTime();
            episode.ApproximateEnd = request.ApproximateEnd?.ToUniversalTime();
            return await CommitAsync(episode, ct);
        }

        /// <summary>关联 / 解除最具体 Strand（null = 解除）。目标必须属于同一 Owner。</summary>
        public async Task<EpisodeResult> RelateEpisodeAsync(
            string ownerId, Guid id, RelateEpisodeRequest request, CancellationToken ct = default)
        {
            var episode = await FindEpisodeAsync(ownerId, id, ct);
            if (episode == null)
                return EpisodeResult.Fail(EpisodeErrorCodes.NotFound, "Episode not found.");
            if (episode.Version != request.ExpectedVersion)
                return VersionConflict();

            if (request.RelatedStrandId is { } strandId
                && !await _db.Strands.AnyAsync(s => s.Id == strandId && s.OwnerId == ownerId, ct))
                return EpisodeResult.Fail(EpisodeErrorCodes.StrandNotFound, "Related strand not found.");

            episode.RelatedStrandId = request.RelatedStrandId;
            return await CommitAsync(episode, ct);
        }

        /// <summary>删除 Episode（硬删，仓库无归档约定）：级联删除其 Probe，不触碰关联 Strand。</summary>
        public async Task<KnowledgeErrorResponse?> DeleteEpisodeAsync(
            string ownerId, Guid id, long expectedVersion, CancellationToken ct = default)
        {
            var episode = await FindEpisodeAsync(ownerId, id, ct);
            if (episode == null)
                return new KnowledgeErrorResponse { Code = EpisodeErrorCodes.NotFound, Message = "Episode not found." };
            if (episode.Version != expectedVersion)
                return VersionConflict().Error;

            _db.Episodes.Remove(episode);
            await _db.SaveChangesAsync(ct);
            return null;
        }

        /// <summary>按日期与/或 Strand 浏览（Owner 隔离），LocalDate 升序、同日按 Id（UUIDv7 时间序）。</summary>
        public async Task<List<EpisodeResponse>> GetEpisodesAsync(
            string ownerId, DateOnly? date = null, Guid? strandId = null, CancellationToken ct = default)
        {
            var query = _db.Episodes.Include(e => e.Probes).Where(e => e.OwnerId == ownerId);
            if (date is { } d) query = query.Where(e => e.LocalDate == d);
            if (strandId is { } sid) query = query.Where(e => e.RelatedStrandId == sid);

            var episodes = await query.OrderBy(e => e.LocalDate).ThenBy(e => e.Id).ToListAsync(ct);
            var strandsById = await OwnerStrandsAsync(ownerId, ct);
            return episodes.Select(e => ToResponse(e, strandsById)).ToList();
        }

        // ===== RecurrenceProbe =====

        /// <summary>
        /// 创建 Probe：谓词走 Matcher 同一 canonicalization。同一 Episode 的同一 canonical
        /// 谓词——活跃则幂等返回既有行；已解决则拒绝（任何解决结果都不再重复发问，ADR-031 §5）。
        /// </summary>
        public async Task<ProbeResult> CreateProbeAsync(
            string ownerId, Guid episodeId, CreateProbeRequest request, CancellationToken ct = default)
        {
            if (MatcherNormalizer.Normalize(request.Matcher) is not { } normalized)
                return ProbeResult.Fail(EpisodeErrorCodes.InvalidMatcher, "A valid matcher is required.");

            var episode = await FindEpisodeAsync(ownerId, episodeId, ct);
            if (episode == null)
                return ProbeResult.Fail(EpisodeErrorCodes.NotFound, "Episode not found.");

            var stepsJson = MatcherCodec.Serialize(normalized.Steps);
            var existing = await _db.RecurrenceProbes.FirstOrDefaultAsync(
                p => p.EpisodeId == episodeId && p.Source == normalized.Source && p.StepsJson == stepsJson, ct);
            if (existing != null)
            {
                return existing.Status == ProbeStatuses.Active
                    ? ProbeResult.Ok(ToResponse(existing))
                    : ProbeResult.Fail(EpisodeErrorCodes.ProbeResolved,
                        "This predicate was already resolved on this episode and will not be re-asked.");
            }

            var probe = new RecurrenceProbe
            {
                Id = Guid.CreateVersion7(),
                OwnerId = ownerId,
                EpisodeId = episodeId,
                Source = normalized.Source,
                StepsJson = stepsJson,
                Status = ProbeStatuses.Active,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.RecurrenceProbes.Add(probe);
            await _db.SaveChangesAsync(ct);
            return ProbeResult.Ok(ToResponse(probe));
        }

        /// <summary>解决 Probe（denied / muted）。promoted 只由提升事务写。已解决不可改判。</summary>
        public async Task<ProbeResult> ResolveProbeAsync(
            string ownerId, Guid probeId, ResolveProbeRequest request, CancellationToken ct = default)
        {
            var resolution = request.Resolution.Trim().ToLowerInvariant();
            if (resolution is not (ProbeStatuses.Denied or ProbeStatuses.Muted))
                return ProbeResult.Fail(EpisodeErrorCodes.InvalidResolution,
                    "Resolution must be 'denied' or 'muted'.");

            var probe = await _db.RecurrenceProbes
                .FirstOrDefaultAsync(p => p.Id == probeId && p.OwnerId == ownerId, ct);
            if (probe == null)
                return ProbeResult.Fail(EpisodeErrorCodes.NotFound, "Probe not found.");
            if (probe.Status != ProbeStatuses.Active)
                return ProbeResult.Fail(EpisodeErrorCodes.ProbeResolved, "Probe is already resolved.");

            probe.Status = resolution;
            probe.ResolvedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return ProbeResult.Ok(ToResponse(probe));
        }

        /// <summary>
        /// 活跃 Probe 清单（Asking 侧的求值输入，ADR-031 §5）：命中只产生发问候选——
        /// 消费方不得据此创建 Episode / Strand 或写任何关联。
        /// </summary>
        public async Task<List<ProbeResponse>> GetActiveProbesAsync(string ownerId, CancellationToken ct = default)
        {
            var probes = await _db.RecurrenceProbes
                .Where(p => p.OwnerId == ownerId && p.Status == ProbeStatuses.Active)
                .OrderBy(p => p.Id)
                .ToListAsync(ct);
            return probes.Select(ToResponse).ToList();
        }

        // ===== Promotion =====

        /// <summary>
        /// 非破坏性提升（ADR-031 §5）：一个事务内——新建或选择 Strand、关联本 Episode、
        /// 可选把 Probe 谓词绑为该 Strand 的 Matcher、把 Probe 解决为 promoted。
        /// 保留 Episode 本体；不自动关联其他历史 Episode；任何失败整批回滚。
        /// </summary>
        public async Task<PromotionResult> PromoteEpisodeAsync(
            string ownerId, Guid episodeId, PromoteEpisodeRequest request, CancellationToken ct = default)
        {
            if (request.ExistingStrandId is null == (request.NewStrand is null))
                return PromotionResult.Fail(EpisodeErrorCodes.InvalidPromotion,
                    "Exactly one of ExistingStrandId or NewStrand is required.");
            if (request.BindProbeMatcher && request.ProbeId is null)
                return PromotionResult.Fail(EpisodeErrorCodes.InvalidPromotion,
                    "BindProbeMatcher requires ProbeId.");

            var episode = await _db.Episodes.Include(e => e.Probes)
                .FirstOrDefaultAsync(e => e.Id == episodeId && e.OwnerId == ownerId, ct);
            if (episode == null)
                return PromotionResult.Fail(EpisodeErrorCodes.NotFound, "Episode not found.");
            if (episode.Version != request.ExpectedVersion)
                return PromotionResult.Fail(EpisodeErrorCodes.VersionConflict,
                    "Episode was modified since it was read. Reload and retry.");

            RecurrenceProbe? probe = null;
            if (request.ProbeId is { } probeId)
            {
                probe = episode.Probes.FirstOrDefault(p => p.Id == probeId);
                if (probe == null)
                    return PromotionResult.Fail(EpisodeErrorCodes.NotFound, "Probe not found on this episode.");
                if (probe.Status != ProbeStatuses.Active)
                    return PromotionResult.Fail(EpisodeErrorCodes.ProbeResolved, "Probe is already resolved.");
            }

            // 提交端（KnowledgeCommitService）把整个 change set 包在一个事务里时加入之，
            // 单独调用时自己开启——两条路径同一套校验与回滚语义。
            var ownsTransaction = _db.Database.CurrentTransaction == null;
            await using var tx = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

            Strand strand;
            if (request.NewStrand is { } create)
            {
                // 新建走 createStrand 的全部校验（父级 / 无环 / 日期 / 同名不重叠）；失败原样透传并回滚。
                var created = await _knowledgeService.CreateStrandAsync(ownerId, create, ct);
                if (created.Error != null)
                    return new PromotionResult(null, created.Error);
                strand = await _db.Strands.Include(s => s.Members)
                    .FirstAsync(s => s.Id == created.Strand!.Id, ct);
            }
            else
            {
                var existing = await _db.Strands.Include(s => s.Members)
                    .FirstOrDefaultAsync(s => s.Id == request.ExistingStrandId && s.OwnerId == ownerId, ct);
                if (existing == null)
                    return PromotionResult.Fail(EpisodeErrorCodes.StrandNotFound, "Target strand not found.");
                strand = existing;
            }

            if (request.BindProbeMatcher)
            {
                // canonical 已存在则收敛（与 Mute 幂等同款纪律），不视为冲突。
                var present = strand.Members.Any(
                    m => m.Source == probe!.Source && m.StepsJson == probe.StepsJson);
                if (!present)
                {
                    strand.Members.Add(new StrandMatcher
                    {
                        Id = Guid.CreateVersion7(),
                        Source = probe!.Source,
                        StepsJson = probe.StepsJson,
                    });
                    strand.Version++;
                    strand.UpdatedAt = _clock.GetUtcNow();
                }
            }

            episode.RelatedStrandId = strand.Id;
            episode.Version++;
            episode.UpdatedAt = _clock.GetUtcNow();

            if (probe != null)
            {
                probe.Status = ProbeStatuses.Promoted;
                probe.ResolvedAt = _clock.GetUtcNow();
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                if (tx != null) await tx.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return PromotionResult.Fail(EpisodeErrorCodes.VersionConflict,
                    "Knowledge was modified concurrently. Reload and retry.");
            }

            var strandResponse = (await _knowledgeService.GetStrandsAsync(ownerId, ct))
                .First(s => s.Id == strand.Id);
            return PromotionResult.Ok(new PromoteEpisodeResponse
            {
                Episode = await ToResponseAsync(episode, ct),
                Strand = strandResponse,
            });
        }

        // ===== helpers =====

        private Task<Episode?> FindEpisodeAsync(string ownerId, Guid id, CancellationToken ct)
            => _db.Episodes.Include(e => e.Probes)
                .FirstOrDefaultAsync(e => e.Id == id && e.OwnerId == ownerId, ct);

        /// <summary>
        /// 近似时间只服务叙事（ADR-031 §4）：都提供时要求顺序正确，且 LocalDate 落在
        /// 起止各自本地日构成的区间内（一致解释）；不做更细的工时校验。
        /// </summary>
        private static EpisodeResult? ValidateTimes(DateOnly localDate, DateTimeOffset? start, DateTimeOffset? end)
        {
            if (start is not { } s || end is not { } e) return null;
            if (s > e)
                return EpisodeResult.Fail(EpisodeErrorCodes.InvalidTimes,
                    "ApproximateStart must not be after ApproximateEnd.");
            if (localDate < DateOnly.FromDateTime(s.Date) || localDate > DateOnly.FromDateTime(e.Date))
                return EpisodeResult.Fail(EpisodeErrorCodes.InvalidTimes,
                    "LocalDate must fall within the approximate time span.");
            return null;
        }

        private async Task<EpisodeResult> CommitAsync(Episode episode, CancellationToken ct)
        {
            episode.Version++;
            episode.UpdatedAt = _clock.GetUtcNow();
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return VersionConflict();
            }
            return EpisodeResult.Ok(await ToResponseAsync(episode, ct));
        }

        private static EpisodeResult VersionConflict()
            => EpisodeResult.Fail(EpisodeErrorCodes.VersionConflict,
                "Episode was modified since it was read. Reload and retry.");

        private async Task<Dictionary<Guid, Strand>> OwnerStrandsAsync(string ownerId, CancellationToken ct)
            => await _db.Strands.AsNoTracking()
                .Where(s => s.OwnerId == ownerId)
                .ToDictionaryAsync(s => s.Id, ct);

        private async Task<EpisodeResponse> ToResponseAsync(Episode episode, CancellationToken ct)
            => ToResponse(episode, await OwnerStrandsAsync(episode.OwnerId, ct));

        private static EpisodeResponse ToResponse(Episode episode, Dictionary<Guid, Strand> strandsById)
        {
            var path = new List<string>();
            if (episode.RelatedStrandId is { } sid)
                for (var cursor = strandsById.GetValueOrDefault(sid); cursor != null;)
                {
                    path.Insert(0, cursor.Name);
                    cursor = cursor.ParentStrandId is { } pid ? strandsById.GetValueOrDefault(pid) : null;
                }
            return new EpisodeResponse
            {
                Id = episode.Id,
                LocalDate = episode.LocalDate,
                Text = episode.Text,
                ApproximateStart = episode.ApproximateStart,
                ApproximateEnd = episode.ApproximateEnd,
                RelatedStrandId = episode.RelatedStrandId,
                RelatedStrandPath = path,
                Version = episode.Version,
                Probes = episode.Probes.OrderBy(p => p.Id).Select(ToResponse).ToList(),
                CreatedAt = episode.CreatedAt,
                UpdatedAt = episode.UpdatedAt,
            };
        }

        private static ProbeResponse ToResponse(RecurrenceProbe probe) => new()
        {
            Id = probe.Id,
            EpisodeId = probe.EpisodeId,
            Matcher = new MatcherDto { Source = probe.Source, Steps = MatcherCodec.Deserialize(probe.StepsJson) },
            Status = probe.Status,
            CreatedAt = probe.CreatedAt,
            ResolvedAt = probe.ResolvedAt,
        };
    }
}
