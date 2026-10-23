using Heartbeat.Core;
using Heartbeat.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Integration.Tests;

public sealed class EntityPersistenceTests : DatabaseTest
{
    [Test]
    public async Task ObservationContextWriteFloorsHistoricalSubMicroseconds()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New();
        await using var dataSource = NpgsqlDataSource.Create(Database.ConnectionString);
        var options = new DbContextOptionsBuilder<HeartbeatDbContext>().UseNpgsql(dataSource).Options;
        var instant = DateTimeOffset.Parse("1999-12-31T23:59:59.9999999Z", System.Globalization.CultureInfo.InvariantCulture);
        await using (var writeContext = new HeartbeatDbContext(options))
        {
            writeContext.Entities.Add(new EntityIndexRow { Id = id, TableName = "observations" });
            writeContext.Observations.Add(new Observation
            {
                Id = id, ObserverId = EntityId.New(), ContentId = EntityId.New(), SchemaId = EntityId.New(),
                StartAt = instant, EndAt = instant,
            });
            await writeContext.SaveChangesAsync(cancellationToken);
        }

        await using var readContext = new HeartbeatDbContext(options);
        var observation = await readContext.Observations.AsNoTracking().SingleAsync(entity => entity.Id == id, cancellationToken);
        var expected = instant.AddTicks(-9);
        await Assert.That(observation.StartAt).IsEqualTo(expected);
        await Assert.That(observation.EndAt).IsEqualTo(expected);
    }
}
