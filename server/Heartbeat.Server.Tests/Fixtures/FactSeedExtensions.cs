using System.Text.Json;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;

namespace Heartbeat.Server.Tests.Fixtures;

/// <summary>Build family records for query tests. Production writes go through FactStore.</summary>
internal static class FactSeedExtensions
{
    internal static void SeedSegments(this AppDbContext db, params ActivitySegment[] activities) => db.SeedSegments((IEnumerable<ActivitySegment>)activities);

    internal static void SeedSegments(this AppDbContext db, IEnumerable<ActivitySegment> activities)
    {
        foreach (var activity in activities)
        {
            var device = activity.Device ?? db.Devices.Find(activity.DeviceId)!;
            var stream = SeedStream(db, device, activity.Source, "segment");
            var appIdentity = activity.AppIdentityId is { } identityId ? db.AppIdentities.Find(identityId) : activity.AppIdentity;
            if (appIdentity is null && activity.AppId is { } appId)
            {
                appIdentity = db.AppIdentities.Local.FirstOrDefault(i => i.AppId == appId)
                    ?? db.AppIdentities.FirstOrDefault(i => i.AppId == appId);
                if (appIdentity is null)
                {
                    appIdentity = new AppIdentity { Key = $"test:app:{appId}", AppId = appId };
                    db.AppIdentities.Add(appIdentity);
                }
            }
            db.Segments.Add(new Segment
            {
                Id = activity.Id,
                OwnerId = device.OwnerId,
                StreamId = stream.StreamId,
                Stream = stream,
                FactId = activity.Id,
                Revision = 1,
                Source = activity.Source,
                TargetKind = "device", TargetId = device.Id,
                AppIdentity = appIdentity,
                StartTime = activity.StartTime,
                EndTime = activity.EndTime,
                Payload = activity.Payload is { } payload ? JsonDocument.Parse(payload) :
                    JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        activityKey = activity.IdentityKey,
                        title = activity.Title,
                        attributes = activity.Attributes is { } attributes ? JsonDocument.Parse(attributes).RootElement : (JsonElement?)null
                    }))
            });
        }
    }

    private static FactStream SeedStream(AppDbContext db, Device device, string source, string kind)
    {
        var stream = db.FactStreams.Local.FirstOrDefault(s => s.Subject.DeviceId == device.Id && s.Source == source && s.FactKind == kind);
        if (stream is not null) return stream;
        var subject = db.FactSubjects.Local.FirstOrDefault(s => s.DeviceId == device.Id)
            ?? db.FactSubjects.FirstOrDefault(s => s.DeviceId == device.Id);
        if (subject is null)
        {
            subject = new FactSubjectRecord { OwnerId = device.OwnerId, SubjectId = Guid.NewGuid(), Device = device, DeviceId = device.Id, Kind = "machine" };
            db.FactSubjects.Add(subject);
        }
        stream = new FactStream
        {
            OwnerId = device.OwnerId,
            StreamId = Guid.NewGuid(),
            SubjectId = subject.SubjectId,
            Subject = subject,
            Source = source,
            FactKind = kind,
            Origin = "legacy-import",
            OutputId = "test-fixture"
        };
        db.FactStreams.Add(stream);
        return stream;
    }
}
