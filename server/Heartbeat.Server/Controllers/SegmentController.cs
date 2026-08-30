using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Data;
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
        UsageService usageService,
        DeviceService deviceService,
        ICurrentUserService currentUser,
        AppDbContext db,
        TimeProvider? timeProvider = null) : ControllerBase
    {
        private readonly UsageService _usageService = usageService;
        private readonly DeviceService _deviceService = deviceService;
        private readonly ICurrentUserService _currentUser = currentUser;
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        [HttpPost]
        [EndpointName("uploadSegments")]
        [RequireHeartbeatProtocol]
        public async Task<IActionResult> Upload([FromBody] SegmentUploadRequest request)
        {
            if (request.Segments == null || request.Segments.Count == 0)
                return BadRequest("Segments cannot be empty.");

            try
            {
                SegmentIngestContract.Validate(request.Segments, _timeProvider.GetUtcNow());
            }
            catch (SegmentIngestContractException ex)
                when (ex.Violation == SegmentIngestContractViolation.LegacyAppName)
            {
                return UpgradeRequiredResult.Create(Response, ex.Message);
            }
            catch (SegmentIngestContractException ex)
            {
                return UnprocessableEntity(ex.Message);
            }

            var userId = _currentUser.GetUserId();
            var hardwareId = Request.Headers[DeviceService.HardwareIdHeader].FirstOrDefault();
            var deviceName = Request.Headers[DeviceService.DeviceNameHeader].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(hardwareId))
                return BadRequest($"Missing {DeviceService.HardwareIdHeader} header.");

            await using var transaction = await db.Database.BeginTransactionAsync();
            try
            {
                var device = await _deviceService.ResolveByHardwareIdAsync(userId, hardwareId, deviceName);
                await _usageService.SaveSegmentsAsync(device.Id, request.Segments);
                await transaction.CommitAsync();
            }
            catch (SegmentIngestContractException ex)
            {
                await transaction.RollbackAsync();
                return UnprocessableEntity(ex.Message);
            }
            return Ok();
        }
    }
}
