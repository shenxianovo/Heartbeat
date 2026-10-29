using Npgsql;
using Testcontainers.PostgreSql;

namespace Orisumi.Testing;

public sealed class PostgresTestInstance : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;
    private readonly SemaphoreSlim _databaseCreation = new(1);
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

        await _databaseCreation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Concurrent DDL across all isolated databases can exhaust PostgreSQL's shared lock table.
            // Only provisioning is serialized; the tests' application reads and writes remain concurrent.
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
        finally { _databaseCreation.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _container.DisposeAsync().ConfigureAwait(false);
        _databaseCreation.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
