using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Data;

namespace Heartbeat.Server.Services;

public interface ISegmentIngestApplicationService
{
    Task IngestAsync(
        string ownerId,
        string hardwareId,
        string? deviceName,
        List<ActivitySegmentItem> segments);
}

/// <summary>
/// Owns the atomic strict Segment ingest use case: validate the whole contract, resolve the
/// request Device, project every Segment, and commit those effects as one transaction.
/// HTTP adapters map its contract failures but do not participate in its unit of work.
/// </summary>
public sealed class SegmentIngestApplicationService(
    AppDbContext db,
    DeviceService deviceService,
    UsageService usageService,
    TimeProvider? timeProvider = null) : ISegmentIngestApplicationService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task IngestAsync(
        string ownerId,
        string hardwareId,
        string? deviceName,
        List<ActivitySegmentItem> segments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        ArgumentNullException.ThrowIfNull(segments);

        // Whole-batch shape/time rejection must precede Device or AppIdentity side effects.
        SegmentIngestContract.Validate(segments, _timeProvider.GetUtcNow());

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var device = await deviceService.ResolveByHardwareIdAsync(ownerId, hardwareId, deviceName);
            await usageService.SaveValidatedSegmentsAsync(device.Id, segments);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
