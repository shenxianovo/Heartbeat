using Heartbeat.Persistence;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Heartbeat.Integration.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-alpine")
        .Build();

    public string AdminConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public abstract class PostgresTestBase(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _databaseName = $"test_{Guid.NewGuid():N}";

    protected string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            Database = _databaseName,
        };
        ConnectionString = builder.ConnectionString;

        await using (var admin = new NpgsqlConnection(fixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    protected HeartbeatDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<HeartbeatDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new HeartbeatDbContext(options);
    }

    protected async Task ProvisionTimelineAsync(Guid ownerId)
    {
        await using var dbContext = CreateDbContext();
        dbContext.Timelines.Add(Timeline.Create(
            ownerId,
            "Personal timeline",
            new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero)));
        await dbContext.SaveChangesAsync();
    }
}

public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
