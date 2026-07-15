using System.IdentityModel.Tokens.Jwt;

namespace Heartbeat.Server.Services
{
    public interface ICurrentUserService
    {
        string GetUserId();

        /// <summary>匿名可达的端点用：无凭证/无 sub 时返回 null 而非抛异常。</summary>
        string? GetUserIdOrNull();

        /// <summary>JWT 的 preferred_username claim；会话 JWT 可能不携带。</summary>
        string? GetUsernameOrNull();
    }

    public class CurrentUserService(IHttpContextAccessor httpContextAccessor) : ICurrentUserService
    {
        public string GetUserId()
        {
            var userId = GetUserIdOrNull();
            if (string.IsNullOrEmpty(userId))
                throw new UnauthorizedAccessException("User ID not found in token.");
            return userId;
        }

        public string? GetUserIdOrNull()
        {
            var userId = httpContextAccessor.HttpContext?.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            return string.IsNullOrEmpty(userId) ? null : userId;
        }

        public string? GetUsernameOrNull()
        {
            var username = httpContextAccessor.HttpContext?.User.FindFirst("preferred_username")?.Value;
            return string.IsNullOrEmpty(username) ? null : username;
        }
    }
}
