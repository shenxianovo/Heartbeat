using Heartbeat.Core.DTOs.Input;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class InputEventService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        /// <summary>
        /// 批量保存输入事件。基于 Id (UUIDv7) 去重，重复上传幂等。
        /// </summary>
        public async Task SaveAsync(long deviceId, InputEventUploadRequest request)
        {
            // 批内按 Id 去重
            var items = request.Events
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .ToList();

            if (items.Count == 0) return;

            // 过滤掉库中已存在的 Id（幂等：重传整批不会重复插入）
            var ids = items.Select(e => e.Id).ToList();
            var existing = await _db.InputEvents
                .Where(e => ids.Contains(e.Id))
                .Select(e => e.Id)
                .ToHashSetAsync();

            var toInsert = items
                .Where(e => !existing.Contains(e.Id))
                .Select(e => new InputEvent
                {
                    Id = e.Id,
                    DeviceId = deviceId,
                    EventType = e.EventType,
                    Code = e.Code,
                    Timestamp = e.Timestamp
                });

            _db.InputEvents.AddRange(toInsert);
            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// 统计某时间段内的键盘/鼠标操作计数。按 (EventType, Code) 分组后映射到响应字段。
        /// </summary>
        public async Task<InputCountsResponse> GetCountsAsync(
            string ownerId, long? deviceId, DateTimeOffset? start, DateTimeOffset? end)
        {
            var query = _db.InputEvents.Where(e => e.Device.OwnerId == ownerId);

            if (deviceId.HasValue)
                query = query.Where(e => e.DeviceId == deviceId.Value);

            if (start.HasValue)
                query = query.Where(e => e.Timestamp >= start.Value);

            if (end.HasValue)
                query = query.Where(e => e.Timestamp < end.Value);

            var groups = await query
                .GroupBy(e => new { e.EventType, e.Code })
                .Select(g => new { g.Key.EventType, g.Key.Code, Count = g.LongCount() })
                .ToListAsync();

            var response = new InputCountsResponse();
            foreach (var g in groups)
            {
                switch (g.EventType)
                {
                    case InputEventType.KeyDown:
                        response.KeyboardTotal += g.Count;
                        break;
                    case InputEventType.MouseButton when g.Code == 1:
                        response.MouseLeft += g.Count;
                        break;
                    case InputEventType.MouseButton when g.Code == 2:
                        response.MouseRight += g.Count;
                        break;
                    case InputEventType.MouseButton when g.Code == 3:
                        response.MouseMiddle += g.Count;
                        break;
                    case InputEventType.MouseScroll when g.Code == 1:
                        response.ScrollUp += g.Count;
                        break;
                    case InputEventType.MouseScroll when g.Code == 2:
                        response.ScrollDown += g.Count;
                        break;
                }
            }

            return response;
        }

        /// <summary>
        /// 统计某时间段内键盘逐键（VK 码）的按下次数。返回全部按键，不裁剪。
        /// </summary>
        public async Task<KeyFrequencyResponse> GetKeyFrequencyAsync(
            string ownerId, long? deviceId, DateTimeOffset? start, DateTimeOffset? end)
        {
            var query = _db.InputEvents
                .Where(e => e.Device.OwnerId == ownerId)
                .Where(e => e.EventType == InputEventType.KeyDown);

            if (deviceId.HasValue)
                query = query.Where(e => e.DeviceId == deviceId.Value);

            if (start.HasValue)
                query = query.Where(e => e.Timestamp >= start.Value);

            if (end.HasValue)
                query = query.Where(e => e.Timestamp < end.Value);

            var keys = await query
                .GroupBy(e => e.Code)
                .Select(g => new KeyFrequencyItem { Code = g.Key, Count = g.LongCount() })
                .OrderByDescending(k => k.Count)
                .ToListAsync();

            return new KeyFrequencyResponse { Keys = keys };
        }
    }
}
