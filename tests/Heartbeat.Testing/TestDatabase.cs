using Heartbeat.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Testing;

public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private bool _disposed;

    internal TestDatabase(
        string adminConnectionString,
        string databaseName,
        string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    internal async Task CreateAsync(CancellationToken cancellationToken)
    {
        await using (var connection = new NpgsqlConnection(_adminConnectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        var options = new DbContextOptionsBuilder<HeartbeatDbContext>()
            .UseNpgsql(dataSource)
            .Options;
        await using var dbContext = new HeartbeatDbContext(options);
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\"";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
