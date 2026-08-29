using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>proposal 编排结果：Proposal 与 Error 互斥。</summary>
    public sealed record ProposalResult(KnowledgeProposalResponse? Proposal, KnowledgeErrorResponse? Error)
    {
        public static ProposalResult Ok(KnowledgeProposalResponse proposal) => new(proposal, null);

        public static ProposalResult Fail(string code, string message)
            => new(null, new KnowledgeErrorResponse { Code = code, Message = message });
    }

    /// <summary>proposal 编排被拒的机器可判代码。</summary>
    public static class ProposalErrorCodes
    {
        public const string QuestionNotFound = "question_not_found";
        public const string QuestionWindowMismatch = "question_window_mismatch";
        public const string EmptyAnswer = "empty_answer";
        public const string GenerationFailed = "generation_failed";

        /// <summary>纠正的目标日期没有任何观察——没有可核对的证据窗口（issue 06）。</summary>
        public const string EmptyDay = "empty_day";
    }

    /// <summary>
    /// 两阶段教学第二步的编排（ADR-031 §6）：零写入——SaveChanges 在这条调用链上不存在。
    /// 主动发问入口按 (Owner, WindowKey, 问题 Id) 取回服务端自己发出的证据卡；Recap 纠正入口
    /// （issue 06）把证据上下文锁定为目标本地日期的活动摘要（与叙事同一份 digest）。
    /// 两个入口共用知识语境装配（已有对象快照，UUIDv7 + 读取时版本）→ LLM 整理 →
    /// 消毒成可编辑 KnowledgeChangeSet。
    /// </summary>
    public class KnowledgeProposalService(
        AppDbContext db, QuestionService questionService, DigestAssembler assembler, IProposalGenerator generator)
    {
        public async Task<ProposalResult> ProposeAsync(
            string ownerId, Guid questionId, ResolvedCalendarWindow window,
            ProposeFromQuestionRequest request, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request.Answer))
                return ProposalResult.Fail(ProposalErrorCodes.EmptyAnswer, "Answer is required.");

            if (!string.Equals(request.WindowKey, window.WindowKey.Value, StringComparison.Ordinal))
                return ProposalResult.Fail(
                    ProposalErrorCodes.QuestionWindowMismatch,
                    "Question belongs to a different Local Calendar Window. Refresh questions before submitting.");

            var question = await questionService.FindQuestionAsync(ownerId, window, questionId, ct);
            if (question == null)
                return ProposalResult.Fail(ProposalErrorCodes.QuestionNotFound,
                    "Question not found for this owner and Local Calendar Window. It may have been adjudicated or regenerated.");

            var context = await LoadContextAsync(
                ownerId, window.CivilStartDateOnly, question.EpisodeId, ct);
            var raw = await generator.ProposeAsync(question, request.Answer, context, ct);
            return Sanitize(raw, context);
        }

        /// <summary>
        /// Recap 纠正入口（issue 06）：证据上下文锁定目标本地日期的 Observation/Segment 窗口。
        /// 不读 Recap 散文——散文只是用户正在纠正的显示上下文，事实证据来自目标日观察与
        /// 用户的话。纠正不直接改正文：提案落知识，目标日由调用方在提交成功后强制重生成。
        /// </summary>
        public async Task<ProposalResult> ProposeCorrectionAsync(
            string ownerId, ResolvedCalendarWindow window,
            ProposeCorrectionRequest request, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request.Correction))
                return ProposalResult.Fail(ProposalErrorCodes.EmptyAnswer, "Correction is required.");

            // 与叙事同一份 digest（同窗口、同深度表、同投影规则）：用户纠正的就是从它生成的回顾。
            var projection = await assembler.AssembleAsync(ownerId, window, ct);
            if (projection.IsEmpty)
                return ProposalResult.Fail(ProposalErrorCodes.EmptyDay,
                    "No observations on this date; there is no recap to correct.");

            var context = await LoadContextAsync(
                ownerId, window.CivilStartDateOnly, sourceEpisodeId: null, ct);
            var raw = await generator.ProposeCorrectionAsync(projection.Digest, request.Correction, context, ct);
            return Sanitize(raw, context);
        }

        private ProposalResult Sanitize(RawKnowledgeProposal? raw, ProposalContext context)
        {
            if (raw == null)
                return ProposalResult.Fail(ProposalErrorCodes.GenerationFailed,
                    "LLM proposal generation failed. Nothing was saved; retry when the upstream recovers.");

            var proposal = ProposalSanitizer.Sanitize(raw, context);
            proposal.ReadingLabels = new Dictionary<string, string>(context.Labels);
            return ProposalResult.Ok(proposal);
        }

        /// <summary>
        /// 知识语境快照（两个入口共用）：全部 Strand（带 path / 日期 / 版本，供按 UUIDv7 选择
        /// 与消歧）、目标叙事日的 Episode ∪ recurrence 问题的源 Episode、全部活跃 Probe。
        /// 版本在此刻读取——sanitizer 盖章、commit 端比对，陈旧提案在提交时显式冲突。
        /// </summary>
        private async Task<ProposalContext> LoadContextAsync(
            string ownerId, DateOnly localDate, Guid? sourceEpisodeId, CancellationToken ct)
        {
            var strandRows = await db.Strands
                .Where(s => s.OwnerId == ownerId)
                .Select(s => new { s.Id, s.ParentStrandId, s.Name, s.Gloss, s.StartedOn, s.EndedOn, s.Version })
                .ToListAsync(ct);
            var byId = strandRows.ToDictionary(s => s.Id);
            var strands = strandRows.Select(s =>
            {
                var path = new List<string>();
                for (var cursor = s; cursor != null;)
                {
                    path.Insert(0, cursor.Name);
                    cursor = cursor.ParentStrandId is { } pid ? byId.GetValueOrDefault(pid) : null;
                }
                return new ProposalStrand(s.Id, path, s.Gloss, s.StartedOn, s.EndedOn, s.Version);
            }).ToList();

            var episodes = await db.Episodes
                .Where(e => e.OwnerId == ownerId
                            && (e.LocalDate == localDate || e.Id == sourceEpisodeId))
                .Select(e => new ProposalEpisode(e.Id, e.LocalDate, e.Text, e.Version))
                .ToListAsync(ct);

            var probes = await db.RecurrenceProbes
                .Where(p => p.OwnerId == ownerId && p.Status == ProbeStatuses.Active)
                .Select(p => new ProposalProbe(p.Id, p.EpisodeId))
                .ToListAsync(ct);

            var depthTables = await assembler.LoadDepthTablesAsync(ct);
            return new ProposalContext(
                strands, episodes, probes, localDate, depthTables.DescribeForPrompt(), depthTables.Labels());
        }
    }
}
