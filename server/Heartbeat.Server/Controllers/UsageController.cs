using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Heartbeat.Server.Services;
using Heartbeat.Core.DTOs;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// 客户端上传使用数据
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/v1/usage")]
    public class UsageController(UsageService service) : ControllerBase
    {
        private readonly UsageService _service = service;

        [HttpPost]
        public async Task<IActionResult> Upload([FromBody] UsageUploadRequest request)
        {
            if (request.Usages == null || request.Usages.Count == 0)
                return BadRequest("Usages cannot be empty.");

            var deviceId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await _service.SaveUsageAsync(deviceId, request);
            return Ok();
        }
    }
}
