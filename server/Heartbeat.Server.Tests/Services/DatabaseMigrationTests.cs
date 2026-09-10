using System.Data.Common;
using Heartbeat.Server.Data;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class DatabaseMigrationTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    protected override string InitialMigration => "20260829100458_AskingWindowIdentity";

    [Fact]
    public async Task DefaultMigrationBudgetIsUnlimitedAndRestoresRequestTimeout()
    {
        var probe = new MigrationProbe(false);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(TestConnectionString, options => options.CommandTimeout(1))
            .AddInterceptors(probe).Options);
        await DatabaseMigration.ApplyAsync(db, NullLogger.Instance);
        Assert.Equal(0, probe.Timeout);
        Assert.Equal(1, db.Database.GetCommandTimeout());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupBudgetAppliesToMigrationAndRestoresRequestTimeout(bool fail)
    {
        var probe = new MigrationProbe(fail);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(TestConnectionString, options => options.CommandTimeout(1))
            .AddInterceptors(probe).Options);

        if (fail)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseMigration.ApplyAsync(db, NullLogger.Instance, 5));
        else
        {
            await DatabaseMigration.ApplyAsync(db, NullLogger.Instance, 5);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }

        Assert.Equal(5, probe.Timeout);
        Assert.Equal(1, db.Database.GetCommandTimeout());
    }

    private sealed class MigrationProbe(bool fail) : DbCommandInterceptor
    {
        public int? Timeout { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Timeout is null && eventData.CommandSource == CommandSource.Migrations)
            {
                Timeout = command.CommandTimeout;
                if (fail) throw new InvalidOperationException("Migration fixture failure");
                // Exercise a real database operation that exceeds the normal request budget.
                command.CommandText = "SELECT pg_sleep(2);\n" + command.CommandText;
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
