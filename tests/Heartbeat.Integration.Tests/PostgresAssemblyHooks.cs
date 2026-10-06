using Heartbeat.Testing;

namespace Heartbeat.Integration.Tests;

public static class PostgresAssemblyHooks
{
    private static PostgresTestInstance? _instance;

    internal static PostgresTestInstance Instance => _instance
        ?? throw new InvalidOperationException("The test PostgreSQL instance has not started.");

    [Before(Assembly)]
    public static async Task StartPostgresAsync(CancellationToken cancellationToken)
    {
        _instance = await PostgresTestInstance.StartAsync(cancellationToken);
    }

    [After(Assembly)]
    public static async Task StopPostgresAsync()
    {
        if (_instance is not null)
        {
            await _instance.DisposeAsync();
            _instance = null;
        }
    }
}
