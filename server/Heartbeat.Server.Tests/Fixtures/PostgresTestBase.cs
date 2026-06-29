using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Heartbeat.Server.Tests.Fixtures;

/// <summary>
/// 每个测试实例创建一个唯一命名的库，应用真实 EF migrations，跑完后 drop。
/// 继承此类并加 [Collection("postgres")] 即可复用共享容器。
/// </summary>
public abstract class PostgresTestBase : IAsyncLifetime
{
    private readonly string _adminConnectionString;
    private readonly string _dbName = $"test_{Guid.NewGuid():N}";
    private string _testConnectionString = string.Empty;

    protected PostgresTestBase(PostgresContainerFixture fixture)
    {
        _adminConnectionString = fixture.AdminConnectionString;
    }

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString);
        var adminDb = builder.Database;
        builder.Database = _dbName;
        _testConnectionString = builder.ConnectionString;

        // 在默认库上创建测试库
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{_dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        // 应用真实 migrations，顺带验证迁移在真库上可用
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();

        await SeedAsync(db);
    }

    /// <summary>子类可重写以在 migrations 之后播种数据。</summary>
    protected virtual Task SeedAsync(AppDbContext db) => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_testConnectionString)
            .Options;

        return new AppDbContext(options);
    }
}
