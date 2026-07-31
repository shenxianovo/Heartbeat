using Heartbeat.Core.DTOs.Knowledge;
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
        ICurrentUserService currentUser) : ControllerBase
    {
        private readonly KnowledgeService _knowledgeService = knowledgeService;
        private readonly QuestionService _questionService = questionService;
        private readonly ICurrentUserService _currentUser = currentUser;

        /// <summary>
        /// 当日候选提问（ADR-029 §4 发问判官）：date 带调用方时区 offset 切日窗口（与 recap 同约）。
        /// 缓存按天 + 水位；已裁决的问题读时 diff 掉。
        /// </summary>
        [HttpGet("questions")]
        [EndpointName("getDailyQuestions")]
        public async Task<ActionResult<DailyQuestionsResponse>> GetDailyQuestions(
            [FromQuery] DateTimeOffset date, CancellationToken ct = default)
        {
            return await _questionService.GetDailyQuestionsAsync(_currentUser.GetUserId(), date, ct);
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
