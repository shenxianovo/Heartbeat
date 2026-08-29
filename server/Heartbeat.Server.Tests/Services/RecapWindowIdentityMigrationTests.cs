using Heartbeat.Server.Data;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// ADR-044 migration keeps derived legacy rows in place and only changes their lookup identity. Applying the
/// migration is schema work: it must not manufacture new Recaps (and therefore cannot imply eager LLM backfill).
/// </summary>
[Collection("postgres")]
public sealed class RecapWindowIdentityMigrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
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
    public async Task MigrationPreservesLegacyRowWithNullWindowIdentityAndCreatesNoBackfillRows()
    {
        var start = DateTimeOffset.Parse("2026-08-18T16:00:00Z");
        using (var db = CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260814045305_AddAppCatalogOverrides");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "Recaps"
                    ("OwnerId", "WindowStart", "Narrative", "GeneratedAt", "Model", "PromptHash", "SegmentWatermark", "KnowledgeHash")
                VALUES
                    ('user-1', {start}, 'legacy narrative', {start}, 'legacy-model', 'deadbeef', {start}, NULL);
                """);
        }

        using (var db = CreateDbContext())
            await db.Database.MigrateAsync();

        using (var db = CreateDbContext())
        {
            var row = await db.Recaps.SingleAsync();
            Assert.Equal("legacy narrative", row.Narrative);
            Assert.Null(row.WindowKey);
            Assert.Null(row.WindowVersion);
            Assert.Null(row.WindowKind);
            Assert.Null(row.LocalDate);
            Assert.Null(row.TimeZone);
            Assert.Null(row.WindowEndExclusive);
            Assert.Equal(1, await db.Recaps.CountAsync());
        }
    }

    private AppDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);
}
