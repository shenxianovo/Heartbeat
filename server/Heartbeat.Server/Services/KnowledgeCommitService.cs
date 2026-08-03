using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>change set 级校验被拒的机器可判代码（ADR-031 §6）。操作级错误沿用领域服务的代码。</summary>
    public static class ChangeSetErrorCodes
    {
        public const string EmptyChangeSet = "empty_changeset";
        public const string DuplicateOpId = "duplicate_op_id";
        public const string UnknownOpType = "unknown_op_type";
        public const string MissingPayload = "missing_payload";
        public const string UnresolvedReference = "unresolved_reference";
        public const string MissingVersion = "missing_version";
    }

    /// <summary>提交结果：Response 与 Error 互斥。Error.FailedOpId 定位具体操作（null = set 级失败）。</summary>
    public sealed record ChangeSetResult(CommitChangeSetResponse? Response, ChangeSetErrorResponse? Error)
    {
        public static ChangeSetResult Ok(CommitChangeSetResponse response) => new(response, null);

        public static ChangeSetResult Fail(string? failedOpId, string code, string message)
            => new(null, new ChangeSetErrorResponse
            {
                FailedOpId = failedOpId,
                Error = new KnowledgeErrorResponse { Code = code, Message = message },
            });

        public static ChangeSetResult Fail(string? failedOpId, KnowledgeErrorResponse error)
            => new(null, new ChangeSetErrorResponse { FailedOpId = failedOpId, Error = error });
    }

    /// <summary>
    /// KnowledgeChangeSet 的共享事务提交端（ADR-031 §6）：主动发问、Recap 纠正与手动复合
    /// 操作的唯一写协议。不信任 LLM 提案或前端编辑——Owner、ID、树、日期、canonical 谓词
    /// 与并发版本全部由既有领域服务（KnowledgeService / EpisodeService）在一个数据库事务里
    /// 重新校验；任一选中操作失败整批回滚，错误定位到 OpId；成功回读真实 UUIDv7 / 版本 / 路径，
    /// 供 UI 替换临时 proposal 引用。
    /// </summary>
    public class KnowledgeCommitService(
        AppDbContext db, KnowledgeService knowledgeService, EpisodeService episodeService,
        TimeProvider? clock = null)
    {
        private readonly AppDbContext _db = db;
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        public async Task<ChangeSetResult> CommitAsync(
            string ownerId, CommitChangeSetRequest request, CancellationToken ct = default)
        {
            if (ValidateShape(request) is { } shapeError) return shapeError;

            var strandIdsByOpId = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var episodeIdsByOpId = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var results = new List<OperationResultResponse>();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            foreach (var op in request.Operations)
            {
                var outcome = await ExecuteAsync(ownerId, op, strandIdsByOpId, episodeIdsByOpId, ct);
                if (outcome.Error != null)
                    return ChangeSetResult.Fail(op.OpId, outcome.Error); // 事务随 using 回滚，无部分写入

                outcome.Result!.OpId = op.OpId;
                outcome.Result.Type = op.Type;
                results.Add(outcome.Result);
            }
            await tx.CommitAsync(ct);

            return ChangeSetResult.Ok(new CommitChangeSetResponse { Results = results });
        }

        /// <summary>
        /// set 级形状校验（纯确定性，事务外）：非空、OpId 唯一、类型已知、payload 与类型匹配、
        /// OpId 临时引用只指向排在前面的正确类型操作。不信 sanitizer——手动入口可直接投喂本端点。
        /// </summary>
        public static ChangeSetResult? ValidateShape(CommitChangeSetRequest request)
        {
            if (request.Operations.Count == 0)
                return ChangeSetResult.Fail(null, ChangeSetErrorCodes.EmptyChangeSet, "Change set has no operations.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var strandOps = new HashSet<string>(StringComparer.Ordinal);
            var episodeOps = new HashSet<string>(StringComparer.Ordinal);

            foreach (var op in request.Operations)
            {
                if (string.IsNullOrWhiteSpace(op.OpId) || !seen.Add(op.OpId))
                    return ChangeSetResult.Fail(op.OpId, ChangeSetErrorCodes.DuplicateOpId,
                        "Each operation needs a unique non-empty opId.");

                var error = op.Type switch
                {
                    KnowledgeOpTypes.CreateStrand => Require(op, op.CreateStrand,
                        p => CheckStrandRef(p.Parent, strandOps, required: false)),
                    KnowledgeOpTypes.UpdateStrand => Require(op, op.UpdateStrand, _ => null),
                    KnowledgeOpTypes.MoveStrand => Require(op, op.MoveStrand,
                        p => CheckStrandRef(p.NewParent, strandOps, required: false)),
                    KnowledgeOpTypes.EndStrand => Require(op, op.EndStrand, _ => null),
                    KnowledgeOpTypes.BindMatcher => Require(op, op.BindMatcher,
                        p => CheckStrandRef(p.Strand, strandOps, required: true)
                             ?? CheckVersion(p.Strand.StrandId, p.ExpectedVersion)),
                    KnowledgeOpTypes.MuteMatcher => Require(op, op.MuteMatcher, _ => null),
                    KnowledgeOpTypes.CreateEpisode => Require(op, op.CreateEpisode,
                        p => CheckStrandRef(p.RelatedStrand, strandOps, required: false)),
                    KnowledgeOpTypes.UpdateEpisode => Require(op, op.UpdateEpisode, _ => null),
                    KnowledgeOpTypes.RelateEpisode => Require(op, op.RelateEpisode,
                        p => CheckEpisodeRef(p.Episode, episodeOps)
                             ?? CheckStrandRef(p.RelatedStrand, strandOps, required: false)
                             ?? CheckVersion(p.Episode.EpisodeId, p.ExpectedVersion)),
                    KnowledgeOpTypes.CreateProbe => Require(op, op.CreateProbe,
                        p => CheckEpisodeRef(p.Episode, episodeOps)),
                    KnowledgeOpTypes.ResolveProbe => Require(op, op.ResolveProbe, _ => null),
                    KnowledgeOpTypes.PromoteEpisode => Require(op, op.PromoteEpisode,
                        p => CheckEpisodeRef(p.Episode, episodeOps)
                             ?? CheckStrandRef(p.Strand, strandOps, required: true)
                             ?? CheckVersion(p.Episode.EpisodeId, p.ExpectedVersion)),
                    _ => ChangeSetResult.Fail(op.OpId, ChangeSetErrorCodes.UnknownOpType,
                        $"Unknown operation type '{op.Type}'."),
                };
                if (error != null) return error;

                if (op.Type == KnowledgeOpTypes.CreateStrand) strandOps.Add(op.OpId);
                if (op.Type == KnowledgeOpTypes.CreateEpisode) episodeOps.Add(op.OpId);
            }
            return null;

            ChangeSetResult? Require<T>(KnowledgeOperationDto op, T? payload, Func<T, ChangeSetResult?> check)
                where T : class
                => payload == null
                    ? ChangeSetResult.Fail(op.OpId, ChangeSetErrorCodes.MissingPayload,
                        $"Operation '{op.Type}' is missing its payload.")
                    : check(payload);

            ChangeSetResult? CheckStrandRef(StrandRefDto? r, HashSet<string> priorOps, bool required)
                => CheckRef(r?.StrandId, r?.OpId, r != null, priorOps, required, "strand");

            ChangeSetResult? CheckEpisodeRef(EpisodeRefDto? r, HashSet<string> priorOps)
                => CheckRef(r?.EpisodeId, r?.OpId, r != null, priorOps, required: true, "episode");

            ChangeSetResult? CheckRef(
                Guid? id, string? refOpId, bool present, HashSet<string> priorOps, bool required, string kind)
            {
                if (!present)
                    return required
                        ? ChangeSetResult.Fail(null, ChangeSetErrorCodes.UnresolvedReference,
                            $"A {kind} reference is required.")
                        : null;
                if (id is null == (refOpId is null))
                    return ChangeSetResult.Fail(null, ChangeSetErrorCodes.UnresolvedReference,
                        $"A {kind} reference needs exactly one of id or opId.");
                if (refOpId != null && !priorOps.Contains(refOpId))
                    return ChangeSetResult.Fail(null, ChangeSetErrorCodes.UnresolvedReference,
                        $"opId '{refOpId}' does not name an earlier create operation of the right kind.");
                return null;
            }

            // 已有对象的修改必须携带读取时版本（ADR-031 §6）；set 内新建对象的版本由提交端推导。
            ChangeSetResult? CheckVersion(Guid? existingId, long? expectedVersion)
                => existingId != null && expectedVersion == null
                    ? ChangeSetResult.Fail(null, ChangeSetErrorCodes.MissingVersion,
                        "Modifying an existing entity requires its read-time version.")
                    : null;
        }

        // ===== 单操作执行 =====

        private sealed record OpOutcome(OperationResultResponse? Result, KnowledgeErrorResponse? Error)
        {
            public static OpOutcome Ok(OperationResultResponse result) => new(result, null);

            public static OpOutcome Fail(KnowledgeErrorResponse error) => new(null, error);
        }

        private async Task<OpOutcome> ExecuteAsync(
            string ownerId, KnowledgeOperationDto op,
            Dictionary<string, Guid> strandIds, Dictionary<string, Guid> episodeIds, CancellationToken ct)
        {
            switch (op.Type)
            {
                case KnowledgeOpTypes.CreateStrand:
                {
                    var p = op.CreateStrand!;
                    var result = await knowledgeService.CreateStrandAsync(ownerId, new CreateStrandRequest
                    {
                        Name = p.Name,
                        Gloss = p.Gloss,
                        ParentStrandId = Resolve(p.Parent, strandIds),
                        StartedOn = p.StartedOn,
                        EndedOn = p.EndedOn,
                        Members = p.Members,
                    }, ct);
                    if (result.Error != null) return OpOutcome.Fail(result.Error);
                    strandIds[op.OpId] = result.Strand!.Id;
                    return OpOutcome.Ok(new OperationResultResponse { Strand = result.Strand });
                }
                case KnowledgeOpTypes.UpdateStrand:
                {
                    var p = op.UpdateStrand!;
                    // Members null = 保留现有指纹（提案不必回显整组成员）；服务层是整组替换语义。
                    var members = p.Members;
                    if (members == null)
                    {
                        var rows = await _db.StrandMatchers
                            .Where(m => m.StrandId == p.StrandId)
                            .Select(m => new { m.Source, m.StepsJson })
                            .ToListAsync(ct);
                        members = rows
                            .Select(m => new MatcherDto { Source = m.Source, Steps = MatcherCodec.Deserialize(m.StepsJson) })
                            .ToList();
                    }
                    var result = await knowledgeService.UpdateStrandAsync(ownerId, p.StrandId, new UpdateStrandRequest
                    {
                        ExpectedVersion = p.ExpectedVersion,
                        Name = p.Name,
                        Gloss = p.Gloss,
                        StartedOn = p.StartedOn,
                        EndedOn = p.EndedOn,
                        Members = members,
                    }, ct);
                    return result.Error != null
                        ? OpOutcome.Fail(result.Error)
                        : OpOutcome.Ok(new OperationResultResponse { Strand = result.Strand });
                }
                case KnowledgeOpTypes.MoveStrand:
                {
                    var p = op.MoveStrand!;
                    var result = await knowledgeService.MoveStrandAsync(ownerId, p.StrandId, new MoveStrandRequest
                    {
                        ExpectedVersion = p.ExpectedVersion,
                        NewParentStrandId = Resolve(p.NewParent, strandIds),
                    }, ct);
                    return result.Error != null
                        ? OpOutcome.Fail(result.Error)
                        : OpOutcome.Ok(new OperationResultResponse { Strand = result.Strand });
                }
                case KnowledgeOpTypes.EndStrand:
                {
                    var p = op.EndStrand!;
                    var result = await knowledgeService.EndStrandAsync(ownerId, p.StrandId, new EndStrandRequest
                    {
                        ExpectedVersion = p.ExpectedVersion,
                        EndedOn = p.EndedOn,
                    }, ct);
                    return result.Error != null
                        ? OpOutcome.Fail(result.Error)
                        : OpOutcome.Ok(new OperationResultResponse { Strand = result.Strand });
                }
                case KnowledgeOpTypes.BindMatcher:
                    return await BindMatcherAsync(ownerId, op.BindMatcher!, strandIds, ct);
                case KnowledgeOpTypes.MuteMatcher:
                {
                    var ok = await knowledgeService.MuteMatcherAsync(ownerId, op.MuteMatcher!.Matcher, ct);
                    return ok
                        ? OpOutcome.Ok(new OperationResultResponse())
                        : OpOutcome.Fail(new KnowledgeErrorResponse
                        {
                            Code = EpisodeErrorCodes.InvalidMatcher,
                            Message = "A valid matcher is required.",
                        });
                }
                case KnowledgeOpTypes.CreateEpisode:
                {
                    var p = op.CreateEpisode!;
                    var result = await episodeService.CreateEpisodeAsync(ownerId, new CreateEpisodeRequest
                    {
                        LocalDate = p.LocalDate,
                        Text = p.Text,
                        ApproximateStart = p.ApproximateStart,
                        ApproximateEnd = p.ApproximateEnd,
                        RelatedStrandId = Resolve(p.RelatedStrand, strandIds),
                    }, ct);
                    if (result.Error != null) return OpOutcome.Fail(result.Error);
                    episodeIds[op.OpId] = result.Episode!.Id;
                    return OpOutcome.Ok(new OperationResultResponse { Episode = result.Episode });
                }
                case KnowledgeOpTypes.UpdateEpisode:
                {
                    var p = op.UpdateEpisode!;
                    var result = await episodeService.UpdateEpisodeAsync(ownerId, p.EpisodeId, new UpdateEpisodeRequest
                    {
                        ExpectedVersion = p.ExpectedVersion,
                        LocalDate = p.LocalDate,
                        Text = p.Text,
                        ApproximateStart = p.ApproximateStart,
                        ApproximateEnd = p.ApproximateEnd,
                    }, ct);
                    return result.Error != null
                        ? OpOutcome.Fail(result.Error)
                        : OpOutcome.Ok(new OperationResultResponse { Episode = result.Episode });
                }
                case KnowledgeOpTypes.RelateEpisode:
                {
                    var p = op.RelateEpisode!;
                    var (episodeId, version, refError) = await ResolveEpisodeAsync(p.Episode, p.ExpectedVersion, episodeIds, ct);
                    if (refError != null) return OpOutcome.Fail(refError);
                    var result = await episodeService.RelateEpisodeAsync(ownerId, episodeId, new RelateEpisodeRequest
                    {
                        ExpectedVersion = version,
                        RelatedStrandId = Resolve(p.RelatedStrand, strandIds),
                    }, ct);
                    return result.Error != null
                        ? OpOutcome.Fail(result.Error)
                        : OpOutcome.Ok(new OperationResultResponse { Episode = result.Episode });
                }
                case KnowledgeOpTypes.CreateProbe:
                {
                    var p = op.CreateProbe!;
                    var (episodeId, _, refError) = await ResolveEpisodeAsync(p.Episode, expectedVersion: 0, episodeIds, ct);
                    if (refError != null) return OpOutcome.Fail(refError);
                    var result = await episodeService.CreateProbeAsync(ownerId, episodeId, new CreateProbeRequest
                    {
                        Matcher = p.Matcher,
                    }, ct);
                    return result.Error != null
                        ? OpOutcome.Fail(result.Error)
                        : OpOutcome.Ok(new OperationResultResponse { Probe = result.Probe });
                }
                case KnowledgeOpTypes.ResolveProbe:
                {
                    var p = op.ResolveProbe!;
                    var result = await episodeService.ResolveProbeAsync(ownerId, p.ProbeId, new ResolveProbeRequest
                    {
                        Resolution = p.Resolution,
                    }, ct);
                    return result.Error != null
                        ? OpOutcome.Fail(result.Error)
                        : OpOutcome.Ok(new OperationResultResponse { Probe = result.Probe });
                }
                case KnowledgeOpTypes.PromoteEpisode:
                {
                    var p = op.PromoteEpisode!;
                    var (episodeId, version, refError) = await ResolveEpisodeAsync(p.Episode, p.ExpectedVersion, episodeIds, ct);
                    if (refError != null) return OpOutcome.Fail(refError);
                    var result = await episodeService.PromoteEpisodeAsync(ownerId, episodeId, new PromoteEpisodeRequest
                    {
                        ExpectedVersion = version,
                        ExistingStrandId = Resolve(p.Strand, strandIds),
                        ProbeId = p.ProbeId,
                        BindProbeMatcher = p.BindProbeMatcher,
                    }, ct);
                    return result.Error != null
                        ? OpOutcome.Fail(result.Error)
                        : OpOutcome.Ok(new OperationResultResponse { Promotion = result.Promotion });
                }
                default:
                    // ValidateShape 已挡住；防御性兜底。
                    return OpOutcome.Fail(new KnowledgeErrorResponse
                    {
                        Code = ChangeSetErrorCodes.UnknownOpType,
                        Message = $"Unknown operation type '{op.Type}'.",
                    });
            }
        }

        /// <summary>为 Strand 追加一个 Matcher：canonical 已存在则收敛（与 Mute / 提升同款幂等纪律）。</summary>
        private async Task<OpOutcome> BindMatcherAsync(
            string ownerId, BindMatcherOpDto p, Dictionary<string, Guid> strandIds, CancellationToken ct)
        {
            if (MatcherNormalizer.Normalize(p.Matcher) is not { } matcher)
                return OpOutcome.Fail(new KnowledgeErrorResponse
                {
                    Code = EpisodeErrorCodes.InvalidMatcher,
                    Message = "A valid matcher is required.",
                });

            var strandId = Resolve(p.Strand, strandIds)!.Value;
            var strand = await _db.Strands.Include(s => s.Members)
                .FirstOrDefaultAsync(s => s.Id == strandId && s.OwnerId == ownerId, ct);
            if (strand == null)
                return OpOutcome.Fail(new KnowledgeErrorResponse
                {
                    Code = KnowledgeErrorCodes.NotFound,
                    Message = "Strand not found.",
                });

            // OpId 引用（set 内新建）的版本由当前事务内实际值推导；已有对象必须匹配读取时版本。
            var expected = p.Strand.OpId != null ? strand.Version : p.ExpectedVersion;
            if (strand.Version != expected)
                return OpOutcome.Fail(new KnowledgeErrorResponse
                {
                    Code = KnowledgeErrorCodes.VersionConflict,
                    Message = "Strand was modified since it was read. Reload and retry.",
                });

            var stepsJson = MatcherCodec.Serialize(matcher.Steps);
            if (!strand.Members.Any(m => m.Source == matcher.Source && m.StepsJson == stepsJson))
            {
                strand.Members.Add(new StrandMatcher
                {
                    Id = Guid.CreateVersion7(),
                    Source = matcher.Source,
                    StepsJson = stepsJson,
                });
                strand.Version++;
                strand.UpdatedAt = _clock.GetUtcNow();
                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return OpOutcome.Fail(new KnowledgeErrorResponse
                    {
                        Code = KnowledgeErrorCodes.VersionConflict,
                        Message = "Strand was modified since it was read. Reload and retry.",
                    });
                }
            }

            var response = (await knowledgeService.GetStrandsAsync(ownerId, ct)).First(s => s.Id == strand.Id);
            return OpOutcome.Ok(new OperationResultResponse { Strand = response });
        }

        private static Guid? Resolve(StrandRefDto? r, Dictionary<string, Guid> strandIds)
            => r == null ? null : r.StrandId ?? strandIds[r.OpId!];

        /// <summary>Episode 引用 → 真实 Id + 有效版本（OpId 引用按事务内实际版本推导）。</summary>
        private async Task<(Guid Id, long Version, KnowledgeErrorResponse? Error)> ResolveEpisodeAsync(
            EpisodeRefDto r, long? expectedVersion, Dictionary<string, Guid> episodeIds, CancellationToken ct)
        {
            if (r.EpisodeId is { } id)
                return (id, expectedVersion ?? 0, null);

            var created = episodeIds[r.OpId!];
            var version = await _db.Episodes.Where(e => e.Id == created).Select(e => e.Version).FirstAsync(ct);
            return (created, version, null);
        }
    }
}
