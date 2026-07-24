using Heartbeat.Core.DTOs.Collectors;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Heartbeat.Server.Controllers
{
    /// <summary>
    /// 采集器声明上行端点（ADR-030 §3/§4）：hub 批量上报观测深度表。
    /// 声明是采集器软件的契约、全局非 per-Owner（多用户信任面留门，ADR-030 Consequences）；
    /// 鉴权与 segments 同姿态——持 ApiKey 的 hub 即可写。
    /// 同 (source, version) 幂等覆盖；坏声明整批 400（批量原子性：hub 的批很小且同源可信）。
    /// </summary>
    [ApiController]
    [Route("api/v1/collectors")]
    [Authorize]
    public class CollectorController(AppDbContext db, TimeProvider? clock = null) : ControllerBase
    {
        private readonly AppDbContext _db = db;
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;

        [HttpPost("declarations")]
        [EndpointName("reportCollectorDeclarations")]
        public async Task<IActionResult> ReportDeclarations(
            [FromBody] List<CollectorDeclarationDto> declarations, CancellationToken ct = default)
        {
            if (declarations == null || declarations.Count == 0)
                return BadRequest("declarations cannot be empty");

            var normalized = new List<CollectorDeclarationDto>(declarations.Count);
            foreach (var declaration in declarations)
            {
                if (DeclarationValidator.Validate(declaration) is { } error)
                    return BadRequest(error);
                normalized.Add(DeclarationValidator.Normalize(declaration));
            }

            var now = _clock.GetUtcNow();
            foreach (var declaration in normalized)
            {
                var row = await _db.CollectorDeclarations.FirstOrDefaultAsync(
                    d => d.Source == declaration.Source && d.Version == declaration.Version, ct);
                if (row == null)
                {
                    row = new CollectorDeclaration { Source = declaration.Source, Version = declaration.Version };
                    _db.CollectorDeclarations.Add(row);
                }
                row.PayloadJson = JsonSerializer.Serialize(declaration);
                row.ReportedAt = now;
            }
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }
    }
}
