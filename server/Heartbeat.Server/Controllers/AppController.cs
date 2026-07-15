using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/apps")]
    [Authorize]
    public class AppController(AppService appService, ICurrentUserService currentUser) : ControllerBase
    {
        private readonly AppService _appService = appService;
        private readonly ICurrentUserService _currentUser = currentUser;

        [HttpGet]
        [EndpointName("getApps")]
        public async Task<List<AppInfoResponse>> List()
        {
            var userId = _currentUser.GetUserId();
            return await _appService.GetAppsForUserAsync(userId);
        }

        [Authorize]
        [HttpPost("icon")]
        [EndpointName("uploadAppIcon")]
        public async Task<IActionResult> UploadIcon([FromBody] IconUploadRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.AppName))
                return BadRequest("AppName is required.");

            if (request.IconData == null || request.IconData.Length == 0)
                return BadRequest("Icon data is required.");

            if (request.IconData.Length > 1024 * 1024)
                return BadRequest("Icon data too large (max 1MB).");

            await _appService.UploadIconAsync(_currentUser.GetUserId(), request.AppName, request.IconData);
            return Ok();
        }
    }
}
