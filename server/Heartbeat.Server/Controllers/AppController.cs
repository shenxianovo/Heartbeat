using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Controllers
{
    [ApiController]
    [Route("api/v1/apps")]
    public class AppController(AppDbContext db) : ControllerBase
    {
        private readonly AppDbContext _db = db;

        /// <summary>
        /// 获取所有应用列表
        /// </summary>
        [HttpGet]
        public async Task<List<AppInfoResponse>> List()
        {
            return await _db.Apps
                .AsNoTracking()
                .Select(a => new AppInfoResponse
                {
                    Id = a.Id,
                    Name = a.Name
                })
                .ToListAsync();
        }

        /// <summary>
        /// 按 AppId 获取应用图标
        /// </summary>
        [HttpGet("{appId:long}/icon")]
        public async Task<IActionResult> GetIcon(long appId)
        {
            var icon = await _db.AppIcons
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.AppId == appId);

            if (icon == null)
                return NotFound();

            return File(icon.IconData, "image/png");
        }

        /// <summary>
        /// 上传应用图标（幂等，已有则覆盖；应用不存在则自动创建）
        /// </summary>
        [Authorize]
        [HttpPost("icon")]
        public async Task<IActionResult> UploadIcon([FromBody] IconUploadRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.AppName))
                return BadRequest("AppName is required.");

            if (request.IconData == null || request.IconData.Length == 0)
                return BadRequest("Icon data is required.");

            if (request.IconData.Length > 1024 * 1024) // 1MB 限制
                return BadRequest("Icon data too large (max 1MB).");

            // 查找或创建 App
            var app = await _db.Apps.FirstOrDefaultAsync(a => a.Name == request.AppName);
            if (app == null)
            {
                app = new App { Name = request.AppName };
                _db.Apps.Add(app);
                await _db.SaveChangesAsync();
            }

            var existing = await _db.AppIcons
                .FirstOrDefaultAsync(x => x.AppId == app.Id);

            if (existing != null)
            {
                existing.IconData = request.IconData;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _db.AppIcons.Add(new AppIcon
                {
                    AppId = app.Id,
                    IconData = request.IconData,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            await _db.SaveChangesAsync();
            return Ok();
        }
    }
}
