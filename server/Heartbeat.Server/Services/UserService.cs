using System.Text.Json;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class UserService(AppDbContext db, IHttpClientFactory httpClientFactory)
    {
        private readonly AppDbContext _db = db;
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

        public async Task<User?> ResolveByUsernameAsync(string username)
        {
            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Username == username);

            if (user != null) return user;

            var resolved = await FetchFromAuthServiceAsync(username);
            if (resolved == null) return null;

            user = new User
            {
                Id = resolved.Value.UserId,
                Username = username,
                LastSeenAt = DateTimeOffset.UtcNow
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            return user;
        }

        private async Task<AuthUserInfo?> FetchFromAuthServiceAsync(string username)
        {
            var client = _httpClientFactory.CreateClient("AuthService");
            var response = await client.GetAsync($"/api/v1/users/{username}");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!json.TryGetProperty("id", out var idProp)) return null;

            return new AuthUserInfo { UserId = idProp.GetString()! };
        }

        private record struct AuthUserInfo
        {
            public string UserId { get; init; }
        }
    }
}
