using Heartbeat.Core;
using Heartbeat.Infrastructure.Database;
using Heartbeat.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Integration.Tests;

public sealed class EntityPersistenceTests
{
    private TestDatabase? _database;

    [Before(Test)]
    public async Task CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        _database = await PostgresAssemblyHooks.Instance.CreateDatabaseAsync(cancellationToken);
    }

    [After(Test)]
    public async Task DeleteDatabaseAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null;
        }
    }

    [Test]
    public async Task SavedObserverCanBeReadFromANewContext()
    {
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var id = EntityId.New();
        await using var dataSource = NpgsqlDataSource.Create(_database!.ConnectionString);
        var options = new DbContextOptionsBuilder<HeartbeatDbContext>()
            .UseNpgsql(dataSource)
            .Options;

        await using (var writeContext = new HeartbeatDbContext(options))
        {
            writeContext.Entities.Add(new EntityIndexRow
            {
                Id = id,
                TableName = "observers",
            });
            writeContext.Observers.Add(new Observer
            {
                Id = id,
                Name = "Integration test observer",
            });
            await writeContext.SaveChangesAsync(cancellationToken);
        }

        await using var readContext = new HeartbeatDbContext(options);
        var observer = await readContext.Observers.AsNoTracking()
            .SingleAsync(entity => entity.Id == id, cancellationToken);
        var index = await readContext.Entities.AsNoTracking()
            .SingleAsync(entity => entity.Id == id, cancellationToken);

        await Assert.That(observer.Id).IsEqualTo(id);
        await Assert.That(observer.Name).IsEqualTo("Integration test observer");
        await Assert.That(index.TableName).IsEqualTo("observers");
    }
}
