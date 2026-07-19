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
    public class KnowledgeController(KnowledgeService knowledgeService, ICurrentUserService currentUser) : ControllerBase
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
