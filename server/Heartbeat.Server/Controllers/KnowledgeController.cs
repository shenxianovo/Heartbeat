using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// 知识裁决端点（ADR-028 §5/§8，单位随 ADR-029 换 Matcher）：Dashboard → Analytics 的写路径
    /// （CONTEXT-MAP 的"一处写例外"）。绑定 Strand / Mute，皆幂等；OwnerId 取 JWT sub，跨 owner 不可达。
    /// 当日发问端点随 ADR-029 issue 03 重建（发问判官）。
    /// </summary>
    [ApiController]
    [Route("api/v1/knowledge")]
    [Authorize]
    public class KnowledgeController(
        KnowledgeService knowledgeService,
        ICurrentUserService currentUser) : ControllerBase
    {
        private readonly KnowledgeService _knowledgeService = knowledgeService;
        private readonly ICurrentUserService _currentUser = currentUser;

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
        [EndpointName("muteMatcher")]
        public async Task<IActionResult> MuteMatcher(
            [FromBody] MuteMatcherRequest request, CancellationToken ct = default)
        {
            var ok = await _knowledgeService.MuteMatcherAsync(_currentUser.GetUserId(), request.Matcher, ct);
            return ok ? NoContent() : BadRequest("A valid matcher is required.");
        }
    }
}
