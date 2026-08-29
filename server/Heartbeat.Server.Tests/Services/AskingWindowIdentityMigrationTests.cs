using Heartbeat.Server.Data;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// ADR-044 keeps legacy question caches as derived history. The schema migration only makes them miss the
/// verified WindowKey identity; it must neither delete them nor manufacture questions through an eager LLM path.
/// </summary>
[Collection("postgres")]
public sealed class AskingWindowIdentityMigrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
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

    [Fact]
    public async Task MigrationPreservesLegacyQuestionRowWithNullWindowIdentityAndCreatesNoBackfillRows()
    {
        var start = DateTimeOffset.Parse("2026-08-18T16:00:00Z");
        using (var db = CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260829083933_RecapWindowIdentity");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $$"""
                INSERT INTO "DailyQuestionSets"
                    ("OwnerId", "WindowStart", "SegmentWatermark", "GeneratedAt", "PayloadVersion", "PayloadJson")
                VALUES
                    ('user-1', {{start}}, {{start}}, {{start}}, 2, '[{"question":"legacy"}]');
                """);
        }

        using (var db = CreateDbContext())
            await db.Database.MigrateAsync();

        using (var db = CreateDbContext())
        {
            var row = await db.DailyQuestionSets.SingleAsync();
            Assert.Contains("legacy", row.PayloadJson);
            Assert.Null(row.WindowKey);
            Assert.Null(row.WindowVersion);
            Assert.Null(row.WindowKind);
            Assert.Null(row.LocalDate);
            Assert.Null(row.TimeZone);
            Assert.Null(row.WindowEndExclusive);
            Assert.Equal(1, await db.DailyQuestionSets.CountAsync());
        }
    }

    private AppDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);
}
