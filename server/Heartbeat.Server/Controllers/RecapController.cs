using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Heartbeat.Core.DTOs.Recaps;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// 每日 Recap 端点（ADR-023 §5，读写按动词拆分随 ADR-042 §2）。owner/public/read/generate
    /// 原子消费同一个经 Analytics 严格验证的 Local Calendar Window（ADR-044）。
    ///
    /// GET 是纯读（零 LLM、零写库），POST 才生成。拆开同时解决三件事：GET 带写副作用的语义污点、
    /// "访客永不触发 LLM"从靠端点升级为靠动词、以及流式响应无法用 GET + EventSource 携带 Bearer。
    /// </summary>
    [ApiController]
    [Route("api/v1/recaps")]
    [Authorize]
    public class RecapController(
        RecapService recapService,
        RecapGenerationLock generationLock,
        ICurrentUserService currentUser) : ControllerBase
    {
        private readonly RecapService _recapService = recapService;
        private readonly RecapGenerationLock _generationLock = generationLock;
        private readonly ICurrentUserService _currentUser = currentUser;

        /// <summary>读取：缓存命中即回，未生成/空日以三态表达，判脏只提示（ADR-042 §3）。</summary>
        [HttpGet("daily")]
        [EndpointName("getDailyRecap")]
        [ProducesResponseType<DailyRecapResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType<CalendarWindowError>(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<DailyRecapResponse>> GetDailyRecap(
            [FromQuery] LocalCalendarWindowEnvelope window,
            CancellationToken ct = default)
        {
            var userId = _currentUser.GetUserId();
            var validation = LocalCalendarWindowValidator.ResolveDay(window);
            if (validation.Error != null) return BadRequest(validation.Error);

            return await _recapService.GetDailyRecapAsync(userId, validation.Window!, ct);
        }

        /// <summary>
        /// 显式生成，SSE 流式响应（ADR-042 §4）。从 OpenAPI 排除：NSwag 无法为流生成有意义的
        /// 签名——响应不是 JSON，前端用 fetch + ReadableStream 手写读流。
        /// </summary>
        [HttpPost("daily/generate")]
        [ApiExplorerSettings(IgnoreApi = true)]
        public IResult GenerateDailyRecap(
            [FromQuery] LocalCalendarWindowEnvelope window,
            CancellationToken ct = default)
        {
            var userId = _currentUser.GetUserId();
            var validation = LocalCalendarWindowValidator.ResolveDay(window);
            if (validation.Error != null) return TypedResults.BadRequest(validation.Error);
            var resolvedWindow = validation.Window!;

            // 409 必须在响应头发出之前判定——流一旦开始，状态码就定了（ADR-042 §7）。
            var lease = _generationLock.TryAcquire(userId, resolvedWindow.WindowKey);
            if (lease == null)
                return TypedResults.Conflict("这一天正在生成中。");

            // 代理层已配 proxy_buffering off；这行是给链路上其他 nginx 的显式声明（见 reverse-proxy runbook）。
            Response.Headers["X-Accel-Buffering"] = "no";

            return TypedResults.ServerSentEvents(StreamAsync(lease, userId, resolvedWindow, ct));
        }

        private async IAsyncEnumerable<SseItem<RecapStreamEvent>> StreamAsync(
            IDisposable lease, string userId, ResolvedCalendarWindow window,
            [EnumeratorCancellation] CancellationToken ct)
        {
            // 租约的寿命就是这条流的寿命（含客户端提前断开）：迭代器 dispose 时归还，下一次生成才能开始。
            using (lease)
            {
                // SSE 结果自己会用 RequestAborted 驱动枚举，[EnumeratorCancellation] 把它并进 ct，
                // 于是"连接断了"与"action 的 ct"是同一件事，无需再 WithCancellation。
                await foreach (var item in _recapService.GenerateDailyRecapStreamAsync(userId, window, ct))
                {
                    yield return new SseItem<RecapStreamEvent>(item, item.Type);
                }
            }
        }
    }
}
