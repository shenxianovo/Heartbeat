using System.Diagnostics;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services;

public static class DatabaseMigration
{
    public static async Task ApplyAsync(AppDbContext db, ILogger logger, int commandTimeoutSeconds = 0,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(commandTimeoutSeconds);
        var requestTimeout = db.Database.GetCommandTimeout();
        var elapsed = Stopwatch.StartNew();
        try
        {
            db.Database.SetCommandTimeout(commandTimeoutSeconds);
            logger.LogInformation("Database migration starting; command timeout {TimeoutSeconds}s (0 = unlimited).",
                commandTimeoutSeconds);
            await RejectUnknownMigrationsAsync(db, ct);
            await db.Database.MigrateAsync(ct);
            // These C# data migrations are part of the upgrade, including when schema is already current.
            logger.LogInformation("Applying knowledge identity backfill.");
            await KnowledgeIdentityBackfill.RunAsync(db, ct);
            logger.LogInformation("Applying App knowledge backfill.");
            await AppKnowledgeBackfill.RunAsync(db, ct);
            logger.LogInformation("Database migration completed in {ElapsedSeconds:F1}s.", elapsed.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database migration failed after {ElapsedSeconds:F1}s; Analytics cannot start.",
                elapsed.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            db.Database.SetCommandTimeout(requestTimeout);
        }
    }

    public static async Task VerifyAsync(AppDbContext db, CancellationToken ct = default)
    {
        await RejectUnknownMigrationsAsync(db, ct);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();
        if (pending.Length > 0)
            throw new InvalidOperationException(
                $"Database migrations are pending: {string.Join(", ", pending)}. Run the deployment migration step before starting Analytics.");
        if (db.Database.HasPendingModelChanges())
            throw new InvalidOperationException("The Analytics model has changes without a database migration.");
    }

    private static async Task RejectUnknownMigrationsAsync(AppDbContext db, CancellationToken ct)
    {
        var unknown = (await db.Database.GetAppliedMigrationsAsync(ct))
            .Except(db.Database.GetMigrations(), StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException(
                $"Database contains migrations absent from this Analytics image: {string.Join(", ", unknown)}. Use the matching release; schema downgrade is not automatic.");
    }
}
