using System.Security.Claims;
using Heartbeat.Server.Services;

namespace Heartbeat.Server.Middleware
{
    public class UserSyncMiddleware(RequestDelegate next)
    {
        private readonly RequestDelegate _next = next;

        public async Task InvokeAsync(HttpContext context, UserService userService)
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var username = context.User.FindFirst("preferred_username")?.Value;

                if (!string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(username))
                {
                    await userService.UpsertAsync(userId, username);
                }
            }

            await _next(context);
        }
    }
}
