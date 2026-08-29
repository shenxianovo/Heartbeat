using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// 知识写路径端点（ADR-028 §5，树/身份随 ADR-031）：Dashboard → Analytics 的"一处写例外"。
    /// Strand 全部按 UUIDv7 定位；不变量校验在 KnowledgeService，此处只做错误码 → HTTP 映射。
    /// OwnerId 取 JWT sub，跨 owner 不可达。
    /// </summary>
    [ApiController]
    [Route("api/v1/knowledge")]
    [Authorize]
    public class KnowledgeController(
        KnowledgeService knowledgeService,
        QuestionService questionService,
        KnowledgeProposalService proposalService,
        KnowledgeCommitService commitService,
        ICurrentUserService currentUser) : ControllerBase
    {
        private readonly KnowledgeService _knowledgeService = knowledgeService;
        private readonly QuestionService _questionService = questionService;
        private readonly KnowledgeProposalService _proposalService = proposalService;
        private readonly KnowledgeCommitService _commitService = commitService;
        private readonly ICurrentUserService _currentUser = currentUser;

        /// <summary>
        /// 当日证据卡问题（ADR-031 §6 两阶段第一步）：完整日窗口先由 Analytics 严格验证。
        /// 缓存按天 + 水位 + payload 版本；已裁决的问题读时 diff 掉；活跃 Probe 命中读时追加。
        /// </summary>
        [HttpGet("questions")]
        [EndpointName("getDailyQuestions")]
        [ProducesResponseType<AskingQuestionsResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<CalendarWindowError>(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<AskingQuestionsResponse>> GetDailyQuestions(
            [FromQuery] LocalCalendarWindowEnvelope window, CancellationToken ct = default)
        {
            var validation = LocalCalendarWindowValidator.ResolveDay(window);
            if (validation.Error != null) return BadRequest(validation.Error);
            return await _questionService.GetDailyQuestionsAsync(
                _currentUser.GetUserId(), validation.Window!, ct);
        }

        /// <summary>
        /// 两阶段教学第二步（ADR-031 §6）：对某张证据卡的自然语言回答 → 可编辑 KnowledgeChangeSet
        /// 提案。请求窗口必须与问题携带的 Analytics WindowKey 匹配。零写入——LLM 只产提案，
        /// 用户确认后走 commit 端点。
        /// </summary>
        [HttpPost("questions/{id:guid}/propose")]
        [EndpointName("proposeFromQuestion")]
        [ProducesResponseType<KnowledgeProposalResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status502BadGateway)]
        public async Task<IActionResult> ProposeFromQuestion(
            Guid id, [FromQuery] LocalCalendarWindowEnvelope window,
            [FromBody] ProposeFromQuestionRequest request, CancellationToken ct = default)
        {
            var validation = LocalCalendarWindowValidator.ResolveDay(window);
            if (validation.Error != null)
                return BadRequest(new KnowledgeErrorResponse
                {
                    Code = validation.Error.Code,
                    Message = validation.Error.Message,
                });
            var result = await _proposalService.ProposeAsync(
                _currentUser.GetUserId(), id, validation.Window!, request, ct);
            if (result.Proposal != null) return Ok(result.Proposal);
            return result.Error!.Code switch
            {
                ProposalErrorCodes.QuestionNotFound => NotFound(result.Error),
                ProposalErrorCodes.GenerationFailed => StatusCode(StatusCodes.Status502BadGateway, result.Error),
                _ => BadRequest(result.Error),
            };
        }

        /// <summary>
        /// Recap 纠正入口（ADR-031 §6，issue 06）：对某日回顾的自然语言纠正 → 可编辑
        /// KnowledgeChangeSet 提案。证据上下文由服务端锁定为该本地日期的活动摘要，
        /// 不接受散文 patch。零写入；确认后走共享 commit 端点，目标日由前端提交成功后
        /// 显式 force 重生成。
        /// </summary>
        [HttpPost("corrections/propose")]
        [EndpointName("proposeCorrection")]
        [ProducesResponseType<KnowledgeProposalResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status502BadGateway)]
        public async Task<IActionResult> ProposeCorrection(
            [FromBody] ProposeCorrectionRequest request, CancellationToken ct = default)
        {
            var result = await _proposalService.ProposeCorrectionAsync(_currentUser.GetUserId(), request, ct);
            if (result.Proposal != null) return Ok(result.Proposal);
            return result.Error!.Code switch
            {
                ProposalErrorCodes.GenerationFailed => StatusCode(StatusCodes.Status502BadGateway, result.Error),
                _ => BadRequest(result.Error),
            };
        }

        /// <summary>
        /// 共享事务提交端（ADR-031 §6）：主动发问、Recap 纠正与手动复合操作共用。
        /// 服务端重新校验全部领域不变量、Owner 与并发版本；选中操作全部成功才提交，
        /// 失败整批回滚并定位到具体 operation。
        /// </summary>
        [HttpPost("changesets")]
        [EndpointName("commitChangeSet")]
        [ProducesResponseType<CommitChangeSetResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<ChangeSetErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ChangeSetErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ChangeSetErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CommitChangeSet(
            [FromBody] CommitChangeSetRequest request, CancellationToken ct = default)
        {
            var result = await _commitService.CommitAsync(_currentUser.GetUserId(), request, ct);
            if (result.Response != null) return Ok(result.Response);
            return result.Error!.Error.Code switch
            {
                KnowledgeErrorCodes.NotFound => NotFound(result.Error),
                KnowledgeErrorCodes.VersionConflict or KnowledgeErrorCodes.ActiveChildren
                    or KnowledgeErrorCodes.Overlap or KnowledgeErrorCodes.Cycle
                    or KnowledgeErrorCodes.ChildrenOutsideRange
                    or EpisodeErrorCodes.ProbeResolved => Conflict(result.Error),
                _ => BadRequest(result.Error),
            };
        }

        /// <summary>整树读取：全部节点（含已结束时期）带 parent ID 与根到自身 path。</summary>
        [HttpGet("strands")]
        [EndpointName("getStrands")]
        public async Task<ActionResult<List<StrandResponse>>> GetStrands(CancellationToken ct = default)
        {
            return await _knowledgeService.GetStrandsAsync(_currentUser.GetUserId(), ct);
        }

        [HttpPost("strands")]
        [EndpointName("createStrand")]
        [ProducesResponseType<StrandResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CreateStrand(
            [FromBody] CreateStrandRequest request, CancellationToken ct = default)
        {
            return ToHttp(await _knowledgeService.CreateStrandAsync(_currentUser.GetUserId(), request, ct));
        }

        [HttpPut("strands/{id:guid}")]
        [EndpointName("updateStrand")]
        [ProducesResponseType<StrandResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> UpdateStrand(
            Guid id, [FromBody] UpdateStrandRequest request, CancellationToken ct = default)
        {
            return ToHttp(await _knowledgeService.UpdateStrandAsync(_currentUser.GetUserId(), id, request, ct));
        }

        [HttpPost("strands/{id:guid}/move")]
        [EndpointName("moveStrand")]
        [ProducesResponseType<StrandResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> MoveStrand(
            Guid id, [FromBody] MoveStrandRequest request, CancellationToken ct = default)
        {
            return ToHttp(await _knowledgeService.MoveStrandAsync(_currentUser.GetUserId(), id, request, ct));
        }

        [HttpPost("strands/{id:guid}/end")]
        [EndpointName("endStrand")]
        [ProducesResponseType<StrandResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> EndStrand(
            Guid id, [FromBody] EndStrandRequest request, CancellationToken ct = default)
        {
            return ToHttp(await _knowledgeService.EndStrandAsync(_currentUser.GetUserId(), id, request, ct));
        }

        [HttpPost("mutes")]
        [EndpointName("muteMatcher")]
        public async Task<IActionResult> MuteMatcher(
            [FromBody] MuteMatcherRequest request, CancellationToken ct = default)
        {
            var ok = await _knowledgeService.MuteMatcherAsync(_currentUser.GetUserId(), request.Matcher, ct);
            return ok ? NoContent() : BadRequest("A valid matcher is required.");
        }

        /// <summary>错误码 → HTTP：查无此行 404；依赖库中现状的冲突 409；请求本身非法 400。</summary>
        private IActionResult ToHttp(KnowledgeResult result)
        {
            if (result.Strand != null) return Ok(result.Strand);
            var error = result.Error!;
            return error.Code switch
            {
                KnowledgeErrorCodes.NotFound => NotFound(error),
                KnowledgeErrorCodes.VersionConflict or KnowledgeErrorCodes.ActiveChildren
                    or KnowledgeErrorCodes.Overlap or KnowledgeErrorCodes.Cycle
                    or KnowledgeErrorCodes.ChildrenOutsideRange => Conflict(error),
                _ => BadRequest(error),
            };
        }
    }
}
