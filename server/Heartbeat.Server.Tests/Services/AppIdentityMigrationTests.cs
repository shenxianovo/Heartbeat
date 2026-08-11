using Heartbeat.Server.Data;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using System.Text.Json;

namespace Heartbeat.Server.Tests.Services;

/// <summary>AppIdentity expand migration 的存量事实保全测试。</summary>
[Collection("postgres")]
public class AppIdentityMigrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _dbName = $"test_{Guid.NewGuid():N}";
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            Database = _dbName
        };
        _connectionString = builder.ConnectionString;

        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{_dbName}\"";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    private AppDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task Migration_BackfillsWindowsIdentity_WithoutRewritingSegmentFact()
    {
        var segmentId = Guid.CreateVersion7();
        var start = new DateTimeOffset(2026, 8, 10, 9, 15, 0, TimeSpan.Zero);
        var end = start.AddMinutes(37);
        const string identityKey = "code\nrepo — main.cs";
        const string attributes = """{"workspace":"heartbeat","line":42}""";

        using (var db = CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260803072137_TwoStageQuestionPayload");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName", "CurrentApp", "LastSeen")
                VALUES (201, 'user-1', 'hw-old', 'Old PC', 'Code', {end});
                INSERT INTO "Apps" ("Id", "Name") VALUES (101, 'Code.exe');
                INSERT INTO "ActivitySegments"
                    ("Id", "DeviceId", "Source", "IdentityKey", "AppId", "Title", "StartTime", "EndTime", "Attributes")
                VALUES
                    ({segmentId}, 201, 'system', {identityKey}, 101, 'repo — main.cs', {start}, {end}, {attributes}::jsonb);
                """);
        }

        using (var db = CreateDbContext())
            await db.Database.MigrateAsync();

        using (var db = CreateDbContext())
        {
            var app = await db.Apps.SingleAsync();
            Assert.Equal("code", app.Key);
            Assert.Equal("Code.exe", app.DisplayName);
            Assert.False(app.IsProvisional);

            var identity = await db.AppIdentities.SingleAsync();
            Assert.Equal("win:code", identity.Key);
            Assert.Equal(app.Id, identity.AppId);

            var segment = await db.ActivitySegments.SingleAsync();
            Assert.Equal(segmentId, segment.Id);
            Assert.Equal(identity.Id, segment.AppIdentityId);
            Assert.Equal(101, segment.AppId);
            Assert.Equal(identityKey, segment.IdentityKey);
            Assert.Equal("system", segment.Source);
            Assert.Equal("repo — main.cs", segment.Title);
            Assert.Equal(start, segment.StartTime);
            Assert.Equal(end, segment.EndTime);
            Assert.True(JsonElement.DeepEquals(
                JsonSerializer.Deserialize<JsonElement>(attributes),
                JsonSerializer.Deserialize<JsonElement>(segment.Attributes!)));
        }
    }

    [Fact]
    public async Task Migration_BackfillsPresenceIdentity_FromLegacyCurrentApp()
    {
        var seen = new DateTimeOffset(2026, 8, 11, 3, 0, 0, TimeSpan.Zero);
        using (var db = CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260811031312_ExpandAppIdentity");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "Devices" ("Id", "OwnerId", "HardwareId", "DeviceName", "CurrentApp", "LastSeen")
                VALUES (301, 'user-1', 'hw-presence', 'PC', 'Code.exe', {seen});
                INSERT INTO "Apps" ("Id", "Key", "DisplayName", "IsProvisional")
                VALUES
                    (300, 'other-code', 'Code.exe', false),
                    (302, 'vscode', 'Code.exe', false);
                INSERT INTO "AppIdentities" ("Id", "Key", "AppId")
                VALUES
                    (301, 'win:other-code', 300),
                    (303, 'win:code', 302);
                """);
        }

        using (var db = CreateDbContext())
            await db.Database.MigrateAsync();

        using (var db = CreateDbContext())
        {
            var device = await db.Devices.Include(x => x.CurrentAppIdentity).SingleAsync();
            Assert.Equal(303, device.CurrentAppIdentityId);
            Assert.Equal("win:code", device.CurrentAppIdentity!.Key);
            Assert.Equal("Code.exe", device.CurrentApp);
        }
    }
}
