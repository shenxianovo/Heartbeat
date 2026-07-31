using Heartbeat.Server.Data;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// HierarchicalTemporalStrand 迁移的数据保全测试（ADR-031 issue 01）：
/// 在含存量 Strand/Matcher/Mute 的库上执行迁移，用户知识零丢失——
/// 原 UUID/名字/Gloss/Matcher 完整保留，迁为日期未知的顶层节点；
/// 自增 Id 换 UUIDv7 后行数与业务身份不变。
/// </summary>
[Collection("postgres")]
public class HierarchicalStrandMigrationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly string _dbName = $"test_{Guid.NewGuid():N}";
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString);
        var adminDb = builder.Database;
        builder.Database = _dbName;
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
    public async Task Migration_PreservesExistingKnowledge_AsTopLevelUndatedNodes()
    {
        var strandId = Guid.CreateVersion7();

        // 迁移到前一版 schema，按旧形状（bigint 自增 Matcher Id、无树/日期列）播种存量知识
        using (var db = CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260722103108_AddCollectorDeclarations");
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Strands" ("Id", "OwnerId", "Name", "Gloss", "CreatedAt", "UpdatedAt")
                VALUES ({0}, 'user-1', 'HyperFrames', '我在搞的 AI 动效框架', now(), now());
                INSERT INTO "StrandMatchers" ("StrandId", "Source", "StepsJson")
                VALUES ({0}, 'system', '[{{"Reading":"app","Op":"equals","Value":"blender.exe"}}]'),
                       ({0}, 'browser', '[{{"Reading":"url","Op":"contains","Value":"localhost:5173"}}]');
                INSERT INTO "MutedMatchers" ("OwnerId", "Source", "StepsJson", "CreatedAt")
                VALUES ('user-1', 'system', '[{{"Reading":"app","Op":"equals","Value":"wechat.exe"}}]', now());
                """, strandId);
        }

        // 执行 HierarchicalTemporalStrand（及其后全部迁移）
        using (var db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        using (var db = CreateDbContext())
        {
            var strand = await db.Strands.Include(s => s.Members).SingleAsync();
            Assert.Equal(strandId, strand.Id);                    // 原 UUID 保留
            Assert.Equal("HyperFrames", strand.Name);
            Assert.Equal("hyperframes", strand.NormalizedName);   // 规范名回填
            Assert.Equal("我在搞的 AI 动效框架", strand.Gloss);
            Assert.Null(strand.ParentStrandId);                   // 顶层
            Assert.Null(strand.StartedOn);                        // 日期未知
            Assert.Null(strand.EndedOn);
            Assert.Equal(1, strand.Version);

            Assert.Equal(2, strand.Members.Count);                // Matcher 零丢失
            Assert.All(strand.Members, m => Assert.NotEqual(Guid.Empty, m.Id));

            var muted = await db.MutedMatchers.SingleAsync();
            Assert.NotEqual(Guid.Empty, muted.Id);
            Assert.Equal("user-1", muted.OwnerId);
        }
    }
}
