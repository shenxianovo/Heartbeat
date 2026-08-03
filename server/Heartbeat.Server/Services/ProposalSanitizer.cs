using System.Globalization;
using Heartbeat.Core.DTOs.Knowledge;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 提案消毒（ADR-031 §6，纯函数）：LLM 原始提案 → 可编辑 KnowledgeChangeSet。
    /// 引用纪律在这一层兑现——已有对象引用必须命中服务端语境快照（虚构/越权 UUID 整条剔除
    /// 并出警告）、并发版本由服务端按读取时快照盖章（不信 LLM 回显）、OpId 临时引用只能指向
    /// 同 set 内排在前面的正确类型操作、Matcher 走同一 canonicalization。
    /// 消毒只裁剪不修补语义：用户没说的字段照 update 语义回填现状。
    /// </summary>
    public static class ProposalSanitizer
    {
        public static KnowledgeProposalResponse Sanitize(RawKnowledgeProposal raw, ProposalContext context)
        {
            var result = new KnowledgeProposalResponse
            {
                Explanation = (raw.Explanation ?? string.Empty).Trim(),
                Suggestions = (raw.Suggestions ?? [])
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .ToList(),
            };

            var strandsById = context.Strands.ToDictionary(s => s.Id);
            var episodesById = context.Episodes.ToDictionary(e => e.Id);
            var probesById = context.Probes.ToDictionary(p => p.Id);

            // 同 set 内可被临时引用的新建操作（类型正确 + 排在前面）。
            var strandOpIds = new HashSet<string>(StringComparer.Ordinal);
            var episodeOpIds = new HashSet<string>(StringComparer.Ordinal);
            var usedOpIds = new HashSet<string>(StringComparer.Ordinal);

            var index = 0;
            foreach (var op in (raw.Operations ?? []).OfType<RawKnowledgeOperation>())
            {
                index++;
                var opId = string.IsNullOrWhiteSpace(op.OpId) ? $"op{index}" : op.OpId.Trim();
                if (!usedOpIds.Add(opId))
                {
                    opId = $"op{index}";
                    usedOpIds.Add(opId);
                }

                var type = (op.Type ?? string.Empty).Trim();
                var sanitized = type switch
                {
                    KnowledgeOpTypes.CreateStrand => CreateStrand(op, strandsById, strandOpIds, result.Warnings),
                    KnowledgeOpTypes.UpdateStrand => UpdateStrand(op, strandsById, result.Warnings),
                    KnowledgeOpTypes.MoveStrand => MoveStrand(op, strandsById, strandOpIds, result.Warnings),
                    KnowledgeOpTypes.EndStrand => EndStrand(op, strandsById, result.Warnings),
                    KnowledgeOpTypes.BindMatcher => BindMatcher(op, strandsById, strandOpIds, result.Warnings),
                    KnowledgeOpTypes.MuteMatcher => MuteMatcher(op, result.Warnings),
                    KnowledgeOpTypes.CreateEpisode => CreateEpisode(op, strandsById, strandOpIds, context, result.Warnings),
                    KnowledgeOpTypes.UpdateEpisode => UpdateEpisode(op, episodesById, result.Warnings),
                    KnowledgeOpTypes.RelateEpisode => RelateEpisode(op, strandsById, strandOpIds, episodesById, episodeOpIds, result.Warnings),
                    KnowledgeOpTypes.CreateProbe => CreateProbe(op, episodesById, episodeOpIds, result.Warnings),
                    KnowledgeOpTypes.ResolveProbe => ResolveProbe(op, probesById, result.Warnings),
                    KnowledgeOpTypes.PromoteEpisode => PromoteEpisode(op, strandsById, strandOpIds, episodesById, episodeOpIds, probesById, result.Warnings),
                    _ => Drop(result.Warnings, $"未知操作类型「{type}」，已剔除。"),
                };
                if (sanitized == null) continue;

                sanitized.OpId = opId;
                sanitized.Type = type;
                result.Operations.Add(sanitized);
                if (type == KnowledgeOpTypes.CreateStrand) strandOpIds.Add(opId);
                if (type == KnowledgeOpTypes.CreateEpisode) episodeOpIds.Add(opId);
            }

            return result;
        }

        private static KnowledgeOperationDto? Drop(List<string> warnings, string reason)
        {
            warnings.Add(reason);
            return null;
        }

        // ===== 各操作 =====

        private static KnowledgeOperationDto? CreateStrand(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalStrand> strands,
            HashSet<string> strandOpIds, List<string> warnings)
        {
            var name = (op.Name ?? string.Empty).Trim();
            if (name.Length == 0)
                return Drop(warnings, "createStrand 缺少名字，已剔除。");

            StrandRefDto? parent = null;
            if (op.ParentStrandId != null || op.ParentOpId != null)
            {
                parent = ResolveStrandRef(op.ParentStrandId, op.ParentOpId, strands, strandOpIds);
                if (parent == null)
                    return Drop(warnings, $"createStrand「{name}」引用了不存在的父级，已剔除。");
            }

            return new KnowledgeOperationDto
            {
                CreateStrand = new CreateStrandOpDto
                {
                    Name = name,
                    Gloss = (op.Gloss ?? string.Empty).Trim(),
                    Parent = parent,
                    StartedOn = ParseDate(op.StartedOn),
                    EndedOn = ParseDate(op.EndedOn),
                    Members = NormalizeMembers(op.Members, warnings, $"createStrand「{name}」"),
                },
            };
        }

        private static KnowledgeOperationDto? UpdateStrand(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalStrand> strands, List<string> warnings)
        {
            if (ResolveExisting(op.StrandId, strands, out var strand) is not { } id)
                return Drop(warnings, "updateStrand 引用了不在已知知识里的 Strand，已剔除。");

            // update 语义：LLM 省略的字段回填现状（用户没说的不动）。版本由服务端盖章。
            return new KnowledgeOperationDto
            {
                UpdateStrand = new UpdateStrandOpDto
                {
                    StrandId = id,
                    ExpectedVersion = strand!.Version,
                    Name = (op.Name ?? strand.Path[^1]).Trim(),
                    Gloss = (op.Gloss ?? strand.Gloss).Trim(),
                    StartedOn = op.StartedOn != null ? ParseDate(op.StartedOn) : strand.StartedOn,
                    EndedOn = op.EndedOn != null ? ParseDate(op.EndedOn) : strand.EndedOn,
                    Members = op.Members == null ? null : NormalizeMembers(op.Members, warnings, "updateStrand"),
                },
            };
        }

        private static KnowledgeOperationDto? MoveStrand(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalStrand> strands,
            HashSet<string> strandOpIds, List<string> warnings)
        {
            if (ResolveExisting(op.StrandId, strands, out var strand) is not { } id)
                return Drop(warnings, "moveStrand 引用了不在已知知识里的 Strand，已剔除。");

            StrandRefDto? newParent = null;
            if (op.NewParentStrandId != null || op.NewParentOpId != null)
            {
                newParent = ResolveStrandRef(op.NewParentStrandId, op.NewParentOpId, strands, strandOpIds);
                if (newParent == null)
                    return Drop(warnings, "moveStrand 引用了不存在的新父级，已剔除。");
            }

            return new KnowledgeOperationDto
            {
                MoveStrand = new MoveStrandOpDto
                {
                    StrandId = id,
                    ExpectedVersion = strand!.Version,
                    NewParent = newParent,
                },
            };
        }

        private static KnowledgeOperationDto? EndStrand(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalStrand> strands, List<string> warnings)
        {
            if (ResolveExisting(op.StrandId, strands, out var strand) is not { } id)
                return Drop(warnings, "endStrand 引用了不在已知知识里的 Strand，已剔除。");
            if (ParseDate(op.EndedOn) is not { } endedOn)
                return Drop(warnings, "endStrand 缺少有效的结束日期，已剔除。");

            return new KnowledgeOperationDto
            {
                EndStrand = new EndStrandOpDto
                {
                    StrandId = id,
                    ExpectedVersion = strand!.Version,
                    EndedOn = endedOn,
                },
            };
        }

        private static KnowledgeOperationDto? BindMatcher(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalStrand> strands,
            HashSet<string> strandOpIds, List<string> warnings)
        {
            var target = ResolveStrandRef(op.StrandId, op.StrandOpId, strands, strandOpIds);
            if (target == null)
                return Drop(warnings, "bindMatcher 引用了不存在的 Strand，已剔除。");
            if (op.Matcher == null || MatcherNormalizer.Normalize(op.Matcher) is not { } matcher)
                return Drop(warnings, "bindMatcher 的指纹无效，已剔除。");

            return new KnowledgeOperationDto
            {
                BindMatcher = new BindMatcherOpDto
                {
                    Strand = target,
                    ExpectedVersion = target.StrandId is { } id ? strands[id].Version : null,
                    Matcher = matcher,
                },
            };
        }

        private static KnowledgeOperationDto? MuteMatcher(RawKnowledgeOperation op, List<string> warnings)
        {
            if (op.Matcher == null || MatcherNormalizer.Normalize(op.Matcher) is not { } matcher)
                return Drop(warnings, "muteMatcher 的指纹无效，已剔除。");
            return new KnowledgeOperationDto { MuteMatcher = new MuteMatcherOpDto { Matcher = matcher } };
        }

        private static KnowledgeOperationDto? CreateEpisode(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalStrand> strands,
            HashSet<string> strandOpIds, ProposalContext context, List<string> warnings)
        {
            var text = (op.Text ?? string.Empty).Trim();
            if (text.Length == 0)
                return Drop(warnings, "createEpisode 缺少内容文本，已剔除。");

            StrandRefDto? related = null;
            if (op.RelatedStrandId != null || op.RelatedOpId != null)
            {
                related = ResolveStrandRef(op.RelatedStrandId, op.RelatedOpId, strands, strandOpIds);
                if (related == null)
                    return Drop(warnings, $"createEpisode「{Truncate(text)}」关联了不存在的 Strand，已剔除。");
            }

            return new KnowledgeOperationDto
            {
                CreateEpisode = new CreateEpisodeOpDto
                {
                    // 缺省/畸形日期落到证据卡所属叙事日——proposal 只解释这一天的证据。
                    LocalDate = ParseDate(op.LocalDate) ?? context.LocalDate,
                    Text = text,
                    ApproximateStart = ParseTime(op.ApproximateStart),
                    ApproximateEnd = ParseTime(op.ApproximateEnd),
                    RelatedStrand = related,
                },
            };
        }

        private static KnowledgeOperationDto? UpdateEpisode(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalEpisode> episodes, List<string> warnings)
        {
            if (ResolveExisting(op.EpisodeId, episodes, out var episode) is not { } id)
                return Drop(warnings, "updateEpisode 引用了不在已知知识里的 Episode，已剔除。");

            return new KnowledgeOperationDto
            {
                UpdateEpisode = new UpdateEpisodeOpDto
                {
                    EpisodeId = id,
                    ExpectedVersion = episode!.Version,
                    LocalDate = ParseDate(op.LocalDate) ?? episode.LocalDate,
                    Text = (op.Text ?? episode.Text).Trim(),
                    ApproximateStart = ParseTime(op.ApproximateStart),
                    ApproximateEnd = ParseTime(op.ApproximateEnd),
                },
            };
        }

        private static KnowledgeOperationDto? RelateEpisode(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalStrand> strands, HashSet<string> strandOpIds,
            Dictionary<Guid, ProposalEpisode> episodes, HashSet<string> episodeOpIds, List<string> warnings)
        {
            var episode = ResolveEpisodeRef(op.EpisodeId, op.EpisodeOpId, episodes, episodeOpIds);
            if (episode == null)
                return Drop(warnings, "relateEpisode 引用了不存在的 Episode，已剔除。");

            StrandRefDto? related = null;
            if (op.RelatedStrandId != null || op.RelatedOpId != null)
            {
                related = ResolveStrandRef(op.RelatedStrandId, op.RelatedOpId, strands, strandOpIds);
                if (related == null)
                    return Drop(warnings, "relateEpisode 关联了不存在的 Strand，已剔除。");
            }

            return new KnowledgeOperationDto
            {
                RelateEpisode = new RelateEpisodeOpDto
                {
                    Episode = episode,
                    ExpectedVersion = episode.EpisodeId is { } id ? episodes[id].Version : null,
                    RelatedStrand = related,
                },
            };
        }

        private static KnowledgeOperationDto? CreateProbe(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalEpisode> episodes,
            HashSet<string> episodeOpIds, List<string> warnings)
        {
            var episode = ResolveEpisodeRef(op.EpisodeId, op.EpisodeOpId, episodes, episodeOpIds);
            if (episode == null)
                return Drop(warnings, "createProbe 引用了不存在的 Episode，已剔除。");
            if (op.Matcher == null || MatcherNormalizer.Normalize(op.Matcher) is not { } matcher)
                return Drop(warnings, "createProbe 的谓词无效，已剔除。");

            return new KnowledgeOperationDto
            {
                CreateProbe = new CreateProbeOpDto { Episode = episode, Matcher = matcher },
            };
        }

        private static KnowledgeOperationDto? ResolveProbe(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalProbe> probes, List<string> warnings)
        {
            if (!TryParseGuid(op.ProbeId, out var probeId) || !probes.ContainsKey(probeId))
                return Drop(warnings, "resolveProbe 引用了不在已知知识里的 Probe，已剔除。");
            var resolution = (op.Resolution ?? string.Empty).Trim().ToLowerInvariant();
            if (resolution is not (ProbeStatuses.Denied or ProbeStatuses.Muted))
                return Drop(warnings, "resolveProbe 的处置只能是 denied 或 muted，已剔除。");

            return new KnowledgeOperationDto
            {
                ResolveProbe = new ResolveProbeOpDto { ProbeId = probeId, Resolution = resolution },
            };
        }

        private static KnowledgeOperationDto? PromoteEpisode(
            RawKnowledgeOperation op, Dictionary<Guid, ProposalStrand> strands, HashSet<string> strandOpIds,
            Dictionary<Guid, ProposalEpisode> episodes, HashSet<string> episodeOpIds,
            Dictionary<Guid, ProposalProbe> probes, List<string> warnings)
        {
            var episode = ResolveEpisodeRef(op.EpisodeId, op.EpisodeOpId, episodes, episodeOpIds);
            if (episode == null)
                return Drop(warnings, "promoteEpisode 引用了不存在的 Episode，已剔除。");
            var strand = ResolveStrandRef(op.StrandId, op.StrandOpId, strands, strandOpIds);
            if (strand == null)
                return Drop(warnings, "promoteEpisode 引用了不存在的 Strand，已剔除。");

            Guid? probeId = null;
            if (op.ProbeId != null)
            {
                if (!TryParseGuid(op.ProbeId, out var pid) || !probes.ContainsKey(pid))
                    return Drop(warnings, "promoteEpisode 引用了不在已知知识里的 Probe，已剔除。");
                probeId = pid;
            }
            if (op.BindProbeMatcher == true && probeId == null)
                return Drop(warnings, "promoteEpisode 要求绑定 Probe 谓词但没有给出 Probe，已剔除。");

            return new KnowledgeOperationDto
            {
                PromoteEpisode = new PromoteEpisodeOpDto
                {
                    Episode = episode,
                    ExpectedVersion = episode.EpisodeId is { } id ? episodes[id].Version : null,
                    Strand = strand,
                    ProbeId = probeId,
                    BindProbeMatcher = op.BindProbeMatcher == true,
                },
            };
        }

        // ===== 引用解析 =====

        private static StrandRefDto? ResolveStrandRef(
            string? strandId, string? opId, Dictionary<Guid, ProposalStrand> strands, HashSet<string> strandOpIds)
        {
            if (TryParseGuid(strandId, out var id) && strands.ContainsKey(id))
                return new StrandRefDto { StrandId = id };
            if (opId != null && strandOpIds.Contains(opId.Trim()))
                return new StrandRefDto { OpId = opId.Trim() };
            return null;
        }

        private static EpisodeRefDto? ResolveEpisodeRef(
            string? episodeId, string? opId, Dictionary<Guid, ProposalEpisode> episodes, HashSet<string> episodeOpIds)
        {
            if (TryParseGuid(episodeId, out var id) && episodes.ContainsKey(id))
                return new EpisodeRefDto { EpisodeId = id };
            if (opId != null && episodeOpIds.Contains(opId.Trim()))
                return new EpisodeRefDto { OpId = opId.Trim() };
            return null;
        }

        private static Guid? ResolveExisting<T>(string? rawId, Dictionary<Guid, T> known, out T? found)
            where T : class
        {
            found = null;
            if (!TryParseGuid(rawId, out var id) || !known.TryGetValue(id, out found)) return null;
            return id;
        }

        private static bool TryParseGuid(string? raw, out Guid id)
            => Guid.TryParse((raw ?? string.Empty).Trim(), out id);

        private static DateOnly? ParseDate(string? raw)
            => DateOnly.TryParseExact((raw ?? string.Empty).Trim(), "yyyy-MM-dd", out var d) ? d : null;

        private static DateTimeOffset? ParseTime(string? raw)
            => DateTimeOffset.TryParse(
                (raw ?? string.Empty).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t : null;

        private static List<MatcherDto> NormalizeMembers(
            List<MatcherDto>? members, List<string> warnings, string opLabel)
        {
            var result = new List<MatcherDto>();
            foreach (var m in members ?? [])
            {
                if (MatcherNormalizer.Normalize(m) is { } normalized) result.Add(normalized);
                else warnings.Add($"{opLabel} 的一个指纹无效，已剔除该指纹。");
            }
            return result;
        }

        private static string Truncate(string s) => s.Length <= 20 ? s : s[..20] + "…";
    }
}
