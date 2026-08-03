using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// Episode / RecurrenceProbe 端点（ADR-031 §4/§5）：知识层的第二组用户确认写路径。
    /// 这里是 Episode 的唯一创建入口——摄入、Matcher / Probe 命中没有任何到达此处的调用边。
    /// 校验在 EpisodeService，此处只做错误码 → HTTP 映射。OwnerId 取 JWT sub，跨 owner 不可达。
    /// </summary>
    [ApiController]
    [Route("api/v1/knowledge")]
    [Authorize]
    public class EpisodeController(EpisodeService episodeService, ICurrentUserService currentUser) : ControllerBase
    {
        private readonly EpisodeService _episodeService = episodeService;
        private readonly ICurrentUserService _currentUser = currentUser;

        /// <summary>按日期与/或关联 Strand 浏览（都可省略 = 全部）。</summary>
        [HttpGet("episodes")]
        [EndpointName("getEpisodes")]
        public async Task<ActionResult<List<EpisodeResponse>>> GetEpisodes(
            [FromQuery] DateOnly? date, [FromQuery] Guid? strandId, CancellationToken ct = default)
        {
            return await _episodeService.GetEpisodesAsync(_currentUser.GetUserId(), date, strandId, ct);
        }

        [HttpPost("episodes")]
        [EndpointName("createEpisode")]
        [ProducesResponseType<EpisodeResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CreateEpisode(
            [FromBody] CreateEpisodeRequest request, CancellationToken ct = default)
        {
            var result = await _episodeService.CreateEpisodeAsync(_currentUser.GetUserId(), request, ct);
            return result.Episode != null ? Ok(result.Episode) : ToHttp(result.Error!);
        }

        [HttpPut("episodes/{id:guid}")]
        [EndpointName("updateEpisode")]
        [ProducesResponseType<EpisodeResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> UpdateEpisode(
            Guid id, [FromBody] UpdateEpisodeRequest request, CancellationToken ct = default)
        {
            var result = await _episodeService.UpdateEpisodeAsync(_currentUser.GetUserId(), id, request, ct);
            return result.Episode != null ? Ok(result.Episode) : ToHttp(result.Error!);
        }

        [HttpPost("episodes/{id:guid}/relate")]
        [EndpointName("relateEpisode")]
        [ProducesResponseType<EpisodeResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> RelateEpisode(
            Guid id, [FromBody] RelateEpisodeRequest request, CancellationToken ct = default)
        {
            var result = await _episodeService.RelateEpisodeAsync(_currentUser.GetUserId(), id, request, ct);
            return result.Episode != null ? Ok(result.Episode) : ToHttp(result.Error!);
        }

        [HttpDelete("episodes/{id:guid}")]
        [EndpointName("deleteEpisode")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> DeleteEpisode(
            Guid id, [FromQuery] long expectedVersion, CancellationToken ct = default)
        {
            var error = await _episodeService.DeleteEpisodeAsync(_currentUser.GetUserId(), id, expectedVersion, ct);
            return error == null ? NoContent() : ToHttp(error);
        }

        [HttpPost("episodes/{id:guid}/probes")]
        [EndpointName("createProbe")]
        [ProducesResponseType<ProbeResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> CreateProbe(
            Guid id, [FromBody] CreateProbeRequest request, CancellationToken ct = default)
        {
            var result = await _episodeService.CreateProbeAsync(_currentUser.GetUserId(), id, request, ct);
            return result.Probe != null ? Ok(result.Probe) : ToHttp(result.Error!);
        }

        [HttpPost("probes/{id:guid}/resolve")]
        [EndpointName("resolveProbe")]
        [ProducesResponseType<ProbeResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> ResolveProbe(
            Guid id, [FromBody] ResolveProbeRequest request, CancellationToken ct = default)
        {
            var result = await _episodeService.ResolveProbeAsync(_currentUser.GetUserId(), id, request, ct);
            return result.Probe != null ? Ok(result.Probe) : ToHttp(result.Error!);
        }

        [HttpPost("episodes/{id:guid}/promote")]
        [EndpointName("promoteEpisode")]
        [ProducesResponseType<PromoteEpisodeResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<KnowledgeErrorResponse>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> PromoteEpisode(
            Guid id, [FromBody] PromoteEpisodeRequest request, CancellationToken ct = default)
        {
            var result = await _episodeService.PromoteEpisodeAsync(_currentUser.GetUserId(), id, request, ct);
            return result.Promotion != null ? Ok(result.Promotion) : ToHttp(result.Error!);
        }

        /// <summary>错误码 → HTTP：与 KnowledgeController 同一分法——查无此行 404；依赖库中现状的冲突 409；请求本身非法 400。</summary>
        private IActionResult ToHttp(KnowledgeErrorResponse error) => error.Code switch
        {
            EpisodeErrorCodes.NotFound => NotFound(error),
            EpisodeErrorCodes.VersionConflict or EpisodeErrorCodes.ProbeResolved
                or KnowledgeErrorCodes.Overlap or KnowledgeErrorCodes.Cycle
                or KnowledgeErrorCodes.ActiveChildren or KnowledgeErrorCodes.ChildrenOutsideRange => Conflict(error),
            _ => BadRequest(error),
        };
    }
}
