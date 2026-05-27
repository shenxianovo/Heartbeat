using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class UserService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        public async Task<User?> GetByUsernameAsync(string username)
        {
            return await _db.Users
                .FirstOrDefaultAsync(u => u.Username == username);
        }

        public async Task UpsertAsync(string userId, string username)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user == null)
            {
                user = new User
                {
                    Id = userId,
                    Username = username,
                    LastSeenAt = DateTimeOffset.UtcNow
                };
                _db.Users.Add(user);
            }
            else
            {
                user.Username = username;
                user.LastSeenAt = DateTimeOffset.UtcNow;
            }

            await _db.SaveChangesAsync();
        }
    }
}
