using Heartbeat.Core.DTOs.Knowledge;
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
        public const string EmptyAnswer = "empty_answer";
        public const string GenerationFailed = "generation_failed";
    }

    /// <summary>
    /// 两阶段教学第二步的编排（ADR-031 §6）：按 (Owner, 日窗口, 问题 Id) 取回服务端自己发出的
    /// 证据卡 → 装配知识语境（已有对象快照，UUIDv7 + 读取时版本）→ LLM 整理 → 消毒成可编辑
    /// KnowledgeChangeSet。全程零写入——SaveChanges 在这条调用链上不存在。
    /// </summary>
    public class KnowledgeProposalService(
        AppDbContext db, QuestionService questionService, DigestAssembler assembler, IProposalGenerator generator)
    {
        public async Task<ProposalResult> ProposeAsync(
            string ownerId, Guid questionId, ProposeFromQuestionRequest request, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request.Answer))
                return ProposalResult.Fail(ProposalErrorCodes.EmptyAnswer, "Answer is required.");

            // 证据引用纪律（ADR-031 §6）：只解释服务端发出过的证据卡，不接受任意 Owner/Segment ID。
            var question = await questionService.FindQuestionAsync(ownerId, request.Date, questionId, ct);
            if (question == null)
                return ProposalResult.Fail(ProposalErrorCodes.QuestionNotFound,
                    "Question not found for this owner and date. It may have been adjudicated or regenerated.");

            var context = await LoadContextAsync(ownerId, question, request.Date, ct);
            var raw = await generator.ProposeAsync(question, request.Answer, context, ct);
            if (raw == null)
                return ProposalResult.Fail(ProposalErrorCodes.GenerationFailed,
                    "LLM proposal generation failed. Nothing was saved; retry when the upstream recovers.");

            var proposal = ProposalSanitizer.Sanitize(raw, context);
            proposal.ReadingLabels = new Dictionary<string, string>(
                (await assembler.LoadDepthTablesAsync(ct)).Labels());
            return ProposalResult.Ok(proposal);
        }

        /// <summary>
        /// 知识语境快照：全部 Strand（带 path / 日期 / 版本，供按 UUIDv7 选择与消歧）、
        /// 目标叙事日的 Episode ∪ recurrence 问题的源 Episode、全部活跃 Probe。
        /// 版本在此刻读取——sanitizer 盖章、commit 端比对，陈旧提案在提交时显式冲突。
        /// </summary>
        private async Task<ProposalContext> LoadContextAsync(
            string ownerId, AskingQuestionResponse question, DateTimeOffset date, CancellationToken ct)
        {
            var localDate = DateOnly.FromDateTime(date.Date);

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
                            && (e.LocalDate == localDate || e.Id == question.EpisodeId))
                .Select(e => new ProposalEpisode(e.Id, e.LocalDate, e.Text, e.Version))
                .ToListAsync(ct);

            var probes = await db.RecurrenceProbes
                .Where(p => p.OwnerId == ownerId && p.Status == ProbeStatuses.Active)
                .Select(p => new ProposalProbe(p.Id, p.EpisodeId))
                .ToListAsync(ct);

            var vocabulary = (await assembler.LoadDepthTablesAsync(ct)).DescribeForPrompt();
            return new ProposalContext(strands, episodes, probes, localDate, vocabulary);
        }
    }
}
