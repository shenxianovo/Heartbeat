using Heartbeat.Core.DTOs.Users;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// 本人视角的用户资源。GET 是懒建供给的触发点（ADR-025）：
    /// 前端登录后调 /me，首次即建行（默认 private），此后回写 username / 刷新 LastSeenAt。
    /// </summary>
    [ApiController]
    [Route("api/v1/me")]
    [Authorize]
    public class MeController(UserService userService, ICurrentUserService currentUser) : ControllerBase
    {
        [HttpGet]
        [EndpointName("getMe")]
        public async Task<ActionResult<MeResponse>> Get()
        {
            var username = currentUser.GetUsernameOrNull();
            // 会话 JWT（Agent）不走 /me；防御：无 preferred_username 的凭证不能供给。
            if (username == null) return Forbid();

            var user = await userService.ProvisionAsync(currentUser.GetUserId(), username);
            return new MeResponse { Username = user.Username, IsPublic = user.IsPublic };
        }

        [HttpPut("settings")]
        [EndpointName("updateMySettings")]
        public async Task<ActionResult<MeResponse>> UpdateSettings([FromBody] UpdateMySettingsRequest request)
        {
            var user = await userService.UpdateVisibilityAsync(currentUser.GetUserId(), request.IsPublic);
            // 行不存在 = 还没经 GET /me 供给过；设置无处落，客户端应先调 GET /me。
            if (user == null) return NotFound();

            return new MeResponse { Username = user.Username, IsPublic = user.IsPublic };
        }
    }
}
