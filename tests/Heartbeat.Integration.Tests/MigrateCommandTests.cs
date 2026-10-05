using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class MigrateCommandTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task ExistingTablesWithoutCurrentMigrationFailPromptlyWithActionableError()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\";", cancellationToken: TestContext.Current.CancellationToken);

        var result = await RunMigrationAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Database initialization failed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project tools/Heartbeat.Dev -- env reset --apply", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentDatabaseCompletesWithoutStartingHttpServer()
    {
        var result = await RunMigrationAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("Now listening", result.StandardOutput, StringComparison.Ordinal);
    }

    private async Task<MigrationResult> RunMigrationAsync()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--migrate");
        start.Environment["ConnectionStrings__Heartbeat"] = ConnectionString;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }

        return new MigrationResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record MigrationResult(int ExitCode, string StandardOutput, string StandardError);
}
