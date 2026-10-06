using Npgsql;
using Testcontainers.PostgreSql;

namespace Heartbeat.Testing;

public sealed class PostgresTestInstance : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;
    private bool _disposed;

    private PostgresTestInstance(PostgreSqlContainer container)
    {
        _container = container;
    }

    public static async Task<PostgresTestInstance> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var container = new PostgreSqlBuilder("postgres:18.6-alpine").Build();
        var instance = new PostgresTestInstance(container);

        try
        {
            await container.StartAsync(cancellationToken).ConfigureAwait(false);
            return instance;
        }
        catch (Exception startupError)
        {
            try
            {
                await instance.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "PostgreSQL startup and cleanup both failed.",
                    startupError,
                    cleanupError);
            }

            throw;
        }
    }

    public async Task<TestDatabase> CreateDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var databaseName = $"test_{Guid.CreateVersion7():N}";
        var containerConnectionString = _container.GetConnectionString();
        var connectionString = new NpgsqlConnectionStringBuilder(containerConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;
        var adminConnectionString = new NpgsqlConnectionStringBuilder(containerConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
        var database = new TestDatabase(adminConnectionString, databaseName, connectionString);

        try
        {
            await database.CreateAsync(cancellationToken).ConfigureAwait(false);
            return database;
        }
        catch (Exception creationError)
        {
            try
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Test database creation and cleanup both failed.",
                    creationError,
                    cleanupError);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _container.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
