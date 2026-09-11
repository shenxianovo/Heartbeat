using System.Diagnostics;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class DatabaseCommandTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260829100458_AskingWindowIdentity";

    [Fact]
    public async Task ProductionStartupRejectsPendingSchemaWithoutApplyingIt()
    {
        await using var db = CreateDbContext();
        var before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var startup = await RunCommand(null);
        Assert.Equal(1, startup.Code);
        Assert.Contains("migration", startup.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Now listening", startup.Output);
        Assert.Equal(before, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task CommandsCheckWithoutWritingAndMigrateWithBothBackfillsWithoutStartingHttp()
    {
        await using var db = CreateDbContext();
        var before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var check = await RunCommand("--check-database");
        Assert.Equal(1, check.Code);
        Assert.Equal(before, await db.Database.GetAppliedMigrationsAsync());

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Apps" ("Key", "DisplayName", "IsProvisional") VALUES ('editor-product', 'Editor', false);
            """);
        db.MutedMatchers.Add(new MutedMatcher
        {
            Id = Guid.NewGuid(), OwnerId = "owner", Source = "system", CreatedAt = DateTimeOffset.UtcNow,
            StepsJson = """[{"Layer":1,"Reading":"app","Op":"equals","Value":"EDITOR"}]"""
        });
        await db.SaveChangesAsync();

        var migration = await RunCommand("--migrate");
        Assert.True(migration.Code == 0, migration.Output);
        Assert.DoesNotContain("Now listening", migration.Output);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        db.ChangeTracker.Clear();
        var matcher = await db.MutedMatchers.SingleAsync();
        Assert.Contains("editor-product", matcher.StepsJson);
        Assert.DoesNotContain("Layer", matcher.StepsJson);
        var repeat = await RunCommand("--migrate");
        Assert.True(repeat.Code == 0, repeat.Output);
        Assert.Equal(matcher.StepsJson, await db.MutedMatchers.Select(x => x.StepsJson).SingleAsync());
        check = await RunCommand("--check-database");
        Assert.True(check.Code == 0, check.Output);

        // A newer database must not silently accept an older application either.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('99999999999999_FutureSchema', '10.0.4');
            """);
        check = await RunCommand("--check-database");
        Assert.Equal(1, check.Code);
        Assert.Contains("99999999999999_FutureSchema", check.Output);
        migration = await RunCommand("--migrate");
        Assert.Equal(1, migration.Code);
    }

    private async Task<(int Code, string Output)> RunCommand(string? command)
    {
        // Empty working directory proves the command does not require App Catalog or auth configuration.
        var directory = Directory.CreateTempSubdirectory("heartbeat-db-command-");
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory.FullName, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(FactController).Assembly.Location);
            if (command is not null) start.ArgumentList.Add(command);
            else
            {
                start.ArgumentList.Add("--contentRoot");
                start.ArgumentList.Add(Path.GetDirectoryName(typeof(FactController).Assembly.Location)!);
                start.Environment["AuthService__Authority"] = "https://auth.invalid";
            }
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["ConnectionStrings__DefaultConnection"] = TestConnectionString;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            return (process.ExitCode, await stdout + await stderr);
        }
        finally { directory.Delete(recursive: true); }
    }
}
