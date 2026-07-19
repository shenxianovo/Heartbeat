using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// 知识裁决端点（ADR-028 §5/§8）：Dashboard → Analytics 的写路径（CONTEXT-MAP 的"一处写例外"）。
    /// 绑定 Strand / Mute 把手，皆幂等；OwnerId 取 JWT sub，跨 owner 不可达。
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

        /// <summary>当日候选提问 + 一次性提案（ADR-028 §4/§5）。date 携带用户时区偏移切日窗口，镜像 recap 契约。</summary>
        [HttpGet("questions")]
        [EndpointName("getDailyQuestions")]
        public async Task<ActionResult<DailyQuestionsResponse>> GetDailyQuestions(
            [FromQuery] DateTimeOffset? date, CancellationToken ct = default)
        {
            var target = date ?? DateTimeOffset.UtcNow;
            return await _questionService.GetDailyQuestionsAsync(_currentUser.GetUserId(), target, ct);
        }

        [HttpPost("strands")]
        [EndpointName("bindStrand")]
        public async Task<ActionResult<StrandResponse>> BindStrand(
            [FromBody] BindStrandRequest request, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return BadRequest("Name is required.");

            var result = await _knowledgeService.BindStrandAsync(_currentUser.GetUserId(), request, ct);
            return result == null ? NotFound() : result;
        }

        [HttpPost("mutes")]
        [EndpointName("muteHandle")]
        public async Task<IActionResult> MuteHandle(
            [FromBody] MuteHandleRequest request, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Token))
                return BadRequest("Source and Token are required.");

            await _knowledgeService.MuteHandleAsync(_currentUser.GetUserId(), request.Source, request.Token, ct);
            return NoContent();
        }
    }
}
