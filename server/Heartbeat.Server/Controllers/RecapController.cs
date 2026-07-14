using Heartbeat.Core.DTOs.Recaps;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// 每日 Recap 端点（ADR-023 §5）。镜像报表契约：date 的 offset 携带用户时区切日窗口。
    /// 无 deviceId——叙事的主语是"你这一天"，跨设备聚合是语义而非默认值。
    /// </summary>
    [ApiController]
    [Route("api/v1/recaps")]
    [Authorize]
    public class RecapController(RecapService recapService, ICurrentUserService currentUser) : ControllerBase
    {
        private readonly RecapService _recapService = recapService;
        private readonly ICurrentUserService _currentUser = currentUser;

        [HttpGet("daily")]
        [EndpointName("getDailyRecap")]
        public async Task<ActionResult<DailyRecapResponse>> GetDailyRecap(
            [FromQuery] DateTimeOffset? date,
            [FromQuery] bool force = false,
            CancellationToken ct = default)
        {
            var userId = _currentUser.GetUserId();
            var targetDate = date ?? DateTimeOffset.UtcNow;

            try
            {
                return await _recapService.GetDailyRecapAsync(userId, targetDate, force, ct);
            }
            catch (RecapGenerationException ex)
            {
                // 失败不写缓存（ADR-023 §4），下次请求自然重试。
                return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
            }
        }
    }
}
