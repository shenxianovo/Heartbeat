using Orisumi.Testing;

namespace Orisumi.Integration.Tests;

public static class DatabaseFixture
{
    private static readonly Lazy<Task<PostgresTestInstance>> Instance = new(() => PostgresTestInstance.StartAsync());
    internal static async Task<TestDatabase> CreateAsync() => await (await Instance.Value).CreateDatabaseAsync();
    [After(Assembly)]
    public static async Task StopAsync()
    {
        if (Instance.IsValueCreated) await (await Instance.Value).DisposeAsync();
    }
}
