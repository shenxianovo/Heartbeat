using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Services;
using Heartbeat.Server.Filters;
using Heartbeat.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// 段上传端点（ADR-017/020）：插件段与 system 段共用，经 Agent 本地枢纽转发。
    /// 不做 source 守卫——防本机进程冒充 system 的守卫在 Agent 枢纽的 loopback 层
    /// （ADR-020 信任澄清：持 ApiKey 者可写任意 source，与旧 /usage 路径同信任姿态）。
    /// </summary>
    [ApiController]
    [Route("api/v1/segments")]
    [Authorize]
    public class SegmentController(
        ISegmentIngestApplicationService ingestService,
        ICurrentUserService currentUser) : ControllerBase
    {
        private readonly ISegmentIngestApplicationService _ingestService = ingestService;
        private readonly ICurrentUserService _currentUser = currentUser;

        [HttpPost]
        [EndpointName("uploadSegments")]
        [RequireHeartbeatProtocol]
        public async Task<IActionResult> Upload([FromBody] SegmentUploadRequest request)
        {
            var userId = _currentUser.GetUserId();
            var hardwareId = Request.Headers[DeviceService.HardwareIdHeader].FirstOrDefault();
            var deviceName = Request.Headers[DeviceService.DeviceNameHeader].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(hardwareId))
                return BadRequest($"Missing {DeviceService.HardwareIdHeader} header.");

            try
            {
                await _ingestService.IngestAsync(userId, hardwareId, deviceName, request.Segments);
            }
            catch (SegmentIngestContractException ex)
                when (ex.Violation == SegmentIngestContractViolation.LegacyAppName)
            {
                return UpgradeRequiredResult.Create(Response, ex.Message);
            }
            catch (SegmentIngestContractException ex)
                when (ex.Violation == SegmentIngestContractViolation.EmptyBatch)
            {
                return BadRequest(ex.Message);
            }
            catch (SegmentIngestContractException ex)
            {
                return UnprocessableEntity(ex.Message);
            }
            return Ok();
        }
    }
}
