using Testcontainers.PostgreSql;
using Xunit;

namespace Heartbeat.Server.Tests.Fixtures;

/// <summary>
/// 整个测试程序集共享一个 PostgreSQL 容器（复用本地 postgres:18-alpine 镜像）。
/// 每个测试方法在该容器内创建独立的库，详见 PostgresTestBase。
/// </summary>
public class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    /// <summary>连接到容器默认库（postgres）的管理连接串，用于 CREATE/DROP DATABASE。</summary>
    public string AdminConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresContainerFixture>;
