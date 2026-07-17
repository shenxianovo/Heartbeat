using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class UserService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        /// <summary>
        /// 匿名按用户名解析：只查本地 Users 表，查不到即 null（上层 404）。
        /// 不回源 Auth 平台、不建行——用户由本人首次带 JWT 的请求供给（ADR-025）。
        /// `~` 开头是驱逐占位值（ADR-027），不可经匿名路径解析。
        /// </summary>
        public async Task<User?> ResolveByUsernameAsync(string username)
        {
            if (username.StartsWith('~'))
                return null;

            return await _db.Users
                .FirstOrDefaultAsync(u => u.Username == username);
        }

        /// <summary>
        /// sub-first 懒建供给（ADR-025）：按 JWT sub 定位，不存在则建行（默认 private），
        /// 存在则回写 username（Auth 平台改名自动同步）并刷新 LastSeenAt。
        /// 回写前驱逐（ADR-027）：该名若被别的 sub 的 stale 行占用（上游改名后旧名被
        /// 新人抢注），将其改为 `~{Id}` 占位——`~` 不在上游字符集故永不撞真名，
        /// 被驱逐者下次登录经本方法自愈。
        /// </summary>
        public async Task<User> ProvisionAsync(string userId, string username)
        {
            var staleHolder = await _db.Users
                .FirstOrDefaultAsync(u => u.Username == username && u.Id != userId);
            if (staleHolder != null)
                staleHolder.Username = "~" + staleHolder.Id;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
            {
                user = new User
                {
                    Id = userId,
                    Username = username,
                    LastSeenAt = DateTimeOffset.UtcNow,
                    IsPublic = false
                };
                _db.Users.Add(user);
            }
            else
            {
                user.Username = username;
                user.LastSeenAt = DateTimeOffset.UtcNow;
            }

            await _db.SaveChangesAsync();
            return user;
        }

        public async Task<User?> UpdateVisibilityAsync(string userId, bool isPublic)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return null;

            user.IsPublic = isPublic;
            await _db.SaveChangesAsync();
            return user;
        }
    }
}
