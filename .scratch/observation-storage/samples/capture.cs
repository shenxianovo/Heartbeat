using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace Heartbeat.Server.Tests.Services;
[Collection("postgres")]
public sealed class ObservationStorageInspection(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260911004949_CompleteHistoricalTargets";
    [Fact]
    public async Task CaptureBeforeAndAfterActualDatabaseRows()
    {
        const string output = "/Users/bytedance/Code/Personal/Heartbeat/.scratch/observation-storage/samples/";
        await using var db = CreateDbContext();
        await using var connection = new NpgsqlConnection(TestConnectionString);
        await connection.OpenAsync();
        await using (var seed = new NpgsqlCommand(await File.ReadAllTextAsync(output + "seed.sql"), connection))
            await seed.ExecuteNonQueryAsync();
        async Task Capture(string name, string[] factTables)
        {
            var result = new JsonObject { ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["kind"] = "synthetic samples in isolated PostgreSQL" };
            result["migrations"] = JsonSerializer.SerializeToNode(await db.Database.GetAppliedMigrationsAsync());
            var tables = new JsonObject();
            foreach (var table in new[] { "Devices", "Apps", "AppIdentities", "ApplicationContexts", "ServiceAccounts", "Persons", "PersonAssociations", "Streams", "Subjects", "FactGaps" }.Concat(factTables))
            {
                await using var command = new NpgsqlCommand($"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text FROM \"{table}\" t", connection);
                tables[table] = JsonNode.Parse((string)(await command.ExecuteScalarAsync())!);
            }
            result["tables"] = tables;
            await File.WriteAllTextAsync(output + name + ".json", result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        await Capture("before", ["Segments", "Events"]);
        await db.Database.MigrateAsync();
        await Capture("after", ["Collectors", "Objects", "Facts", "Relations", "RelationMembers"]);
        Assert.Equal(9, await db.Facts.CountAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
