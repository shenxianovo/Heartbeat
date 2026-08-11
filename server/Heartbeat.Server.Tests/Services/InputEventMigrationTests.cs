using Heartbeat.Core.DTOs.Input;
using Heartbeat.Server.Data;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

/// <summary>CodeSet expand migration 对历史 Windows 输入事实的保全测试。</summary>
[Collection("postgres")]
public sealed class InputEventMigrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _dbName = $"test_{Guid.NewGuid():N}";
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString) { Database = _dbName };
        _connectionString = builder.ConnectionString;
        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{_dbName}\"";
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private AppDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task Migration_TagsHistoricalRowsAsWindowsVk_WithoutRewritingRawCode()
    {
        var id = Guid.CreateVersion7();
        var timestamp = new DateTimeOffset(2026, 8, 11, 3, 0, 0, TimeSpan.Zero);
        using (var db = CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260811035550_CompleteAppConsumersAndAdminMerge");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName")
                VALUES (701, 'user-1', 'hw-input', 'Old PC');
                INSERT INTO "InputEvents" ("Id", "DeviceId", "EventType", "Code", "Timestamp")
                VALUES ({id}, 701, 1, 65, {timestamp});
                """);
        }

        using (var db = CreateDbContext())
            await db.Database.MigrateAsync();

        using (var db = CreateDbContext())
        {
            var input = await db.InputEvents.SingleAsync();
            Assert.Equal(id, input.Id);
            Assert.Equal(InputCodeSets.WindowsVirtualKeyV1, input.CodeSet);
            Assert.Equal((short)65, input.Code);
            Assert.Equal(timestamp, input.Timestamp);
        }
    }
}
